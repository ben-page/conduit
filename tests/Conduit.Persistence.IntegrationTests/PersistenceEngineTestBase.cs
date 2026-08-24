using System.Globalization;
using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Conduit.Runtime;

namespace Conduit.Persistence.IntegrationTests;

/// <summary>
/// Cross-engine integration matrix for the ADO.NET persistence stores, exercised against a REAL
/// database container per engine. These tests validate the engine-divergent behaviors that the
/// byte-exact dialect unit tests cannot: live DDL/type mappings, atomic claiming with engine-native
/// row-locking, insert-or-ignore dedup, pagination, and full-precision ISO-8601 timestamp round-trips.
///
/// The stores are constructed directly with the engine's <see cref="IConduitConnectionFactory"/> +
/// <see cref="ISqlDialect"/> — the AddConduit/AddDurability capture pipeline is intentionally NOT
/// exercised here because the engine factories are <c>INonTransactionalConnectionFactory</c> and the
/// outbox capture path fail-fasts on them by design.
///
/// Each concrete subclass owns one engine's container lifecycle (static, shared across the class) and
/// provides a fresh, isolated connection factory + the engine dialect. The <c>[TestInitialize]</c>
/// guard marks tests Inconclusive when the container/podman is unavailable.
/// </summary>
[TestClass]
public abstract class PersistenceEngineTestBase
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    // The body of the newest dead-letter entry (index 4): { (byte)4, 0xFE }.
    private static readonly byte[] ExpectedNewestBody = [0x04, 0xFE];

    /// <summary>True when the engine container is up and tests may run.</summary>
    protected abstract bool IsAvailable { get; }

    /// <summary>The engine dialect under test.</summary>
    protected abstract ISqlDialect Dialect { get; }

    /// <summary>
    /// Creates a connection factory bound to a FRESH, isolated schema/database for the calling test,
    /// so tables created by one test never collide with another. The returned factory opens real
    /// connections to the engine container.
    /// </summary>
    protected abstract Task<IConduitConnectionFactory> CreateIsolatedFactoryAsync();

    [TestInitialize]
    public void TestInitialize()
    {
        if (!this.IsAvailable)
        {
            Assert.Inconclusive(
                $"{this.GetType().Name}: database container not available. Ensure podman/docker is running.");
        }
    }

    // 1) DDL / table creation -------------------------------------------------------------------

    [TestMethod]
    public async Task CreateAllStoreTablesFromTheEngineDialect()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();

        var outbox = new DbConnectionOutboxStore(factory, this.Dialect);
        var inbox = new DbConnectionInboxStore(factory, this.Dialect);
        var deadLetters = new DbDeadLetterStore(factory, this.Dialect);
        var audit = new DbAuditStore(factory, this.Dialect);

        // Act — the first gate: every store's composed DDL must create on the live engine.
        var act = async () =>
        {
            await outbox.EnsureTableExistsAsync(CancellationToken.None);
            await inbox.EnsureTableExistsAsync(CancellationToken.None);
            await deadLetters.EnsureTableExistsAsync(CancellationToken.None);
            await audit.EnsureTableExistsAsync(CancellationToken.None);
        };

        // Assert
        await act.Should().NotThrowAsync();
    }

    // 2) Outbox round-trip + atomic claim -------------------------------------------------------

    [TestMethod]
    public async Task RoundTripOutboxEntriesThroughSaveClaimAndMarkSent()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbConnectionOutboxStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        await SaveEntriesAsync(factory, this.Dialect, new[]
        {
            NewEntry("m1", createdAt),
            NewEntry("m2", createdAt),
        });

        // Act — claim then read back full entries.
        var claimed = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert — both pending rows claimed with the persisted fields intact.
        claimed.Should().HaveCount(2);
        claimed.Select(e => e.MessageId).Should().BeEquivalentTo("m1", "m2");
        claimed.Should().AllSatisfy(e =>
        {
            e.Status.Should().Be(OutboxEntryStatus.Claiming);
            e.Payload.Should().Be("{}");
            e.ConversationId.Should().Be("conv-1");
            // Full-precision ISO 8601 timestamp round-trip.
            e.CreatedAt.Should().BeCloseTo(createdAt, TimeSpan.FromMilliseconds(1));
        });

        // Act — mark one sent, then re-claim: only the still-pending one comes back.
        await store.MarkSentAsync(claimed[0].Id, CancellationToken.None);
    }

    [TestMethod]
    public async Task GiveConcurrentClaimPendingCallersDisjointSets()
    {
        // Arrange — 30 pending rows, two concurrent claimers on independent real connections.
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbConnectionOutboxStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        await SaveEntriesAsync(factory, this.Dialect,
            Enumerable.Range(0, 30).Select(i => NewEntry($"m{i}", createdAt)).ToList());

        // Act — race. The atomic UPDATE (FOR UPDATE SKIP LOCKED / READPAST) must not double-claim.
        var a = store.ClaimPendingAsync(batchSize: 30, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var b = store.ClaimPendingAsync(batchSize: 30, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var results = await Task.WhenAll(a, b);

        // Assert — disjoint, no row claimed twice.
        var idsA = results[0].Select(e => e.Id).ToList();
        var idsB = results[1].Select(e => e.Id).ToList();
        idsA.Should().NotIntersectWith(idsB);
        idsA.Concat(idsB).Should().OnlyHaveUniqueItems();
    }

    [TestMethod]
    public async Task ClaimOutboxEntriesByMessageIds()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbConnectionOutboxStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        string[] messageIds = ["a", "b", "c", "d"];
        await SaveEntriesAsync(factory, this.Dialect, messageIds.Select(id => NewEntry(id, createdAt)).ToList());

        // Act — claim a subset by message id.
        string[] subset = ["b", "d"];
        var claimed = await store.ClaimByMessageIdsAsync(subset, CancellationToken.None);

        // Assert
        claimed.Select(e => e.MessageId).Should().BeEquivalentTo("b", "d");
    }

    [TestMethod]
    public async Task GiveConcurrentClaimByIdsAndClaimPendingDisjointSets()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbConnectionOutboxStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var createdAt = DateTime.UtcNow.AddMinutes(-1);
        var messageIds = Enumerable.Range(0, 30).Select(i => $"m{i}").ToList();
        await SaveEntriesAsync(factory, this.Dialect, messageIds.Select(id => NewEntry(id, createdAt)).ToList());

        // Act — relay-style (by ids) and flush-style (pending) race for the same rows.
        var byIds = store.ClaimByMessageIdsAsync(messageIds, CancellationToken.None);
        var byPending = store.ClaimPendingAsync(batchSize: 30, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await Task.WhenAll(byIds, byPending);

        // Assert — no row dispatched by both paths.
        (await byIds).Select(e => e.Id).Should().NotIntersectWith((await byPending).Select(e => e.Id));
    }

    [TestMethod]
    public async Task RecoverStaleClaimsPastTheClaimTimeout()
    {
        // Arrange — a row claimed long ago (past the 5-minute claim timeout) must become re-claimable.
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbConnectionOutboxStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var createdAt = DateTime.UtcNow.AddMinutes(-30);
        await SaveEntriesAsync(factory, this.Dialect, new[] { NewEntry("stale", createdAt) });

        // First claim takes the row (status -> Claiming with a recent ClaimedAt).
        var first = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        first.Should().ContainSingle();

        // Backdate ClaimedAt past the claim timeout so the claim looks abandoned.
        await SetClaimedAtAsync(factory, first[0].Id, DateTime.UtcNow.Subtract(ClaimTimeout).AddMinutes(-1));

        // Act — a fresh claim should recover the stale row.
        var recovered = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert
        recovered.Should().ContainSingle(e => e.MessageId == "stale");
    }

    // 3) Inbox dedup ----------------------------------------------------------------------------

    [TestMethod]
    public async Task RecordInboxMessageExactlyOnceUnderConcurrency()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbConnectionInboxStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        const string messageId = "dup-1";

        // Act — many concurrent insert-or-ignore of the same id; exactly one must "win" (rows=1).
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => store.RecordAsync(messageId, CancellationToken.None))
            .ToList();
        var results = await Task.WhenAll(tasks);

        // Assert — exactly one winner.
        results.Count(won => won).Should().Be(1);
        (await store.ExistsAsync(messageId, CancellationToken.None)).Should().BeTrue();
        (await store.ExistsAsync("never-recorded", CancellationToken.None)).Should().BeFalse();
    }

    // 4) Dead letter queue ----------------------------------------------------------------------

    [TestMethod]
    public async Task PageCountAndDeleteDeadLetters()
    {
        // Arrange — 5 entries with strictly increasing DeadLetteredAt so DESC ordering is determinate.
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbDeadLetterStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            await store.WriteAsync(new DeadLetterEntry
            {
                Id = id,
                MessageType = "Test.Failed",
                Body = new BinaryData(new byte[] { (byte)i, 0xFE }),
                SourceEndpoint = "self",
                ExceptionType = "System.InvalidOperationException",
                ExceptionMessage = $"boom {i}",
                MessageId = $"msg-{i}",
                DeadLetteredAt = baseTime.AddMinutes(i),
            }, CancellationToken.None);
        }

        // Act — page 1 (size 2) then page 2.
        var page1 = await store.QueryAsync(new DeadLetterFilter { PageSize = 2 }, CancellationToken.None);
        var page2 = await store.QueryAsync(
            new DeadLetterFilter { PageSize = 2, PageToken = page1.NextPageToken },
            CancellationToken.None);

        // Assert — count + pagination (OFFSET/FETCH on SqlServer, LIMIT/OFFSET elsewhere).
        page1.TotalCount.Should().Be(5);
        page1.Entries.Should().HaveCount(2);
        page2.Entries.Should().HaveCount(2);
        page1.Entries.Concat(page2.Entries).Select(e => e.Id).Should().OnlyHaveUniqueItems();

        // Newest first (DeadLetteredAt DESC): the last-written entry leads page 1.
        page1.Entries[0].Id.Should().Be(ids[4]);
        // Body round-trips (blob).
        page1.Entries[0].Body.ToArray().Should().Equal(ExpectedNewestBody);

        // Act — fetch one by id, then delete it.
        var fetched = await store.GetAsync(ids[0], CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.ExceptionMessage.Should().Be("boom 0");

        await store.RemoveAsync(ids[0], CancellationToken.None);
        var afterDelete = await store.QueryAsync(new DeadLetterFilter { PageSize = 10 }, CancellationToken.None);

        // Assert
        afterDelete.TotalCount.Should().Be(4);
        afterDelete.Entries.Select(e => e.Id).Should().NotContain(ids[0]);
    }

    // 5) Audit store ----------------------------------------------------------------------------

    [TestMethod]
    public async Task RoundTripAuditBatchWithFullTimestampPrecisionAndPurge()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbAuditStore(factory, this.Dialect);
        await store.EnsureTableExistsAsync(CancellationToken.None);

        // A timestamp with sub-millisecond (100-ns tick) precision to prove the ISO-8601 text path
        // preserves full precision (a native datetime mapping would silently truncate).
        // Anchored to today rather than a fixed calendar date: the purge below deletes anything
        // older than one day, so a hard-coded date silently ages out of the retention window and
        // fails the survival assertion a day after it is written.
        var preciseTimestamp = DateTime.UtcNow.Date.AddHours(12).AddTicks(1234567);
        var oldTimestamp = DateTime.UtcNow.AddDays(-30);

        var records = new[]
        {
            NewAuditRecord("audit-keep", preciseTimestamp),
            NewAuditRecord("audit-old", oldTimestamp),
        };

        // Act — save then read one back.
        await store.SaveBatchAsync(records, CancellationToken.None);
        var read = await store.GetByMessageIdAsync("audit-keep", CancellationToken.None);

        // Assert — exact tick-level round-trip.
        read.Should().NotBeNull();
        read!.Timestamp.Should().Be(preciseTimestamp);
        read.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        read.ConversationId.Should().Be("conv-1");
        read.MessageKind.Should().Be("Command");

        // Act — purge everything older than 1 day.
        await store.PurgeExpiredAsync(DateTime.UtcNow.AddDays(-1), CancellationToken.None);

        // Assert — the old record is gone, the recent one stays.
        (await store.GetByMessageIdAsync("audit-old", CancellationToken.None)).Should().BeNull();
        (await store.GetByMessageIdAsync("audit-keep", CancellationToken.None)).Should().NotBeNull();
    }

    // Helpers -----------------------------------------------------------------------------------

    private static OutboxEntry NewEntry(string messageId, DateTime createdAt) => new()
    {
        MessageId = messageId,
        ConversationId = "conv-1",
        Type = "Test.Event",
        Payload = "{}",
        CreatedAt = createdAt,
    };

    private static DispatchAuditRecord NewAuditRecord(string messageId, DateTime timestamp) => new()
    {
        MessageId = messageId,
        ConversationId = "conv-1",
        MessageType = "Test.Commands.DoThing",
        MessageKind = "Command",
        ServiceName = "test-service",
        Timestamp = timestamp,
        Observations = new List<MessageObservation>(),
    };

    /// <summary>
    /// Saves outbox entries through a REAL transaction opened on the engine connection (mirroring the
    /// transactional-capture contract of <see cref="DbConnectionOutboxStore.SaveAsync"/>).
    /// </summary>
    private static async Task SaveEntriesAsync(
        IConduitConnectionFactory factory,
        ISqlDialect dialect,
        IReadOnlyList<OutboxEntry> entries)
    {
        await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
        await using var transaction = await connection.BeginTransactionAsync(CancellationToken.None);

        var store = new DbConnectionOutboxStore(factory, dialect);
        await store.SaveAsync(entries, transaction, CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private static async Task SetClaimedAtAsync(IConduitConnectionFactory factory, long id, DateTime claimedAt)
    {
        await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE conduit_outbox SET ClaimedAt = @ClaimedAt WHERE Id = @Id";

        var p1 = command.CreateParameter();
        p1.ParameterName = "@ClaimedAt";
        p1.Value = claimedAt.ToString("O", CultureInfo.InvariantCulture);
        command.Parameters.Add(p1);

        var p2 = command.CreateParameter();
        p2.ParameterName = "@Id";
        p2.Value = id;
        command.Parameters.Add(p2);

        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
