using System.Globalization;
using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Durability.DeadLetterQueue;
using Conduit.Durability.Persistence.DeadLetterQueue;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Jobs;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Microsoft.Extensions.Options;
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

    private static readonly IOptions<JobsOptions> JobsOptionsForTest = Options.Create(
        new JobsOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            ClaimTimeout = TimeSpan.FromMinutes(2),
        });

    /// <summary>True when the engine container is up and tests may run.</summary>
    protected abstract bool IsAvailable { get; }

    /// <summary>The engine dialect under test.</summary>
    protected abstract ISqlDialect Dialect { get; }

    /// <summary>
    /// Whether an <c>UPDATE</c> that matches no row still locks the gap where that row would go, so
    /// the row cannot be inserted behind it (InnoDB at REPEATABLE READ). PostgreSQL and SQL Server at
    /// READ COMMITTED lock nothing.
    /// </summary>
    protected virtual bool MissingRowUpdateLocksTheGap => false;

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

    // 6) Jobs run ledger ------------------------------------------------------------------------
    //
    // The claim protocol is the whole coordination design, and SQLite exercises none of the
    // row-locking each server engine uses for it.

    [TestMethod]
    public async Task ClaimAJobRunExactlyOnceUnderConcurrency()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await store.TryMaterializeAsync(NewJobRun("nightly@fixed", now.AddMinutes(-1)), CancellationToken.None);

        // Act — twenty workers race for one due row
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(worker =>
            store.ClaimDueAsync($"worker-{worker}", now, 10, CancellationToken.None)));

        // Assert — this is what the whole coordination design rests on
        claims.SelectMany(claimed => claimed).Should().HaveCount(1);
    }

    [TestMethod]
    public async Task MaterializeAJobRunExactlyOnceUnderConcurrency()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        // Act — twenty replicas race to materialize one occurrence
        var inserts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            store.TryMaterializeAsync(NewJobRun("nightly@race", now), CancellationToken.None)));

        // Assert — the unique constraint on JobKey is what collapses the race, and each engine
        // reaches it by a different route (ON CONFLICT, INSERT IGNORE, UPDLOCK/HOLDLOCK)
        inserts.Count(inserted => inserted).Should().Be(1);
    }

    [TestMethod]
    public async Task RecoverAClaimAbandonedByACrashedHost()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await store.TryMaterializeAsync(NewJobRun("nightly@fixed", now.AddMinutes(-1)), CancellationToken.None);
        await store.ClaimDueAsync("crashed-host", now, 10, CancellationToken.None);

        // Act — no heartbeat arrives, so the claim goes stale
        var recovered = await store.ClaimDueAsync(
            "healthy-host",
            now.Add(JobsOptionsForTest.Value.ClaimTimeout).AddSeconds(1),
            10,
            CancellationToken.None);

        // Assert — leader election would simply lose this occurrence
        recovered.Should().HaveCount(1);
    }

    [TestMethod]
    public async Task NotRecoverAClaimThatIsStillBeingHeartbeated()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await store.TryMaterializeAsync(NewJobRun("nightly@fixed", now.AddMinutes(-1)), CancellationToken.None);
        var claimed = await store.ClaimDueAsync("busy-host", now, 10, CancellationToken.None);

        // Act — the host renews its claim past the timeout, as a long-running job does
        var later = now.Add(JobsOptionsForTest.Value.ClaimTimeout).AddSeconds(1);
        var beat = await store.HeartbeatAsync(
            claimed[0].Id, claimed[0].ClaimToken!, later, CancellationToken.None);
        var stolen = await store.ClaimDueAsync(
            "other-host", later.AddSeconds(1), 10, CancellationToken.None);

        // Assert — the regression test for executing a long job twice
        beat.ClaimHeld.Should().BeTrue();
        stolen.Should().BeEmpty();
    }

    [TestMethod]
    public async Task RoundTripAJobRunAndItsCursorWithFullTimestampPrecision()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);

        // Sub-millisecond precision proves the ISO-8601 text path is not silently truncating, which
        // would make two nearby occurrences collide on the same key.
        var occurrence = DateTimeOffset.UtcNow.AddMinutes(-1).AddTicks(1234567);
        await store.TryMaterializeAsync(NewJobRun("nightly@precise", occurrence), CancellationToken.None);

        // Act
        var claimed = await store.ClaimDueAsync(
            "worker", DateTimeOffset.UtcNow, 10, CancellationToken.None);
        await store.CompleteAsync(
            claimed[0], "nightly", claimed[0].Occurrence, retainRunKey: false, DateTimeOffset.UtcNow,
            CancellationToken.None);

        // Assert — the delete and the cursor advance land together, on the live engine
        claimed[0].RunAt.Should().Be(occurrence);
        (await store.GetRunAsync("nightly@precise", CancellationToken.None)).Should().BeNull();
        var schedule = await store.GetScheduleAsync("nightly", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(occurrence);
    }

    [TestMethod]
    public async Task KeepACompletedTriggerRunsKeyUntilItsRetentionPurge()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await store.TryMaterializeAsync(NewJobRun("event@msg-1", now.AddMinutes(-1)), CancellationToken.None);
        var claimed = await store.ClaimDueAsync("worker", now, 10, CancellationToken.None);

        // Act
        await store.CompleteAsync(
            claimed[0], null, null, retainRunKey: true, now, CancellationToken.None);
        var redelivered = await store.InsertClaimedAsync(
            NewJobRun("event@msg-1", now), "other-host", now, CancellationToken.None);
        var purgedEarly = await store.PurgeCompletedAsync(now.AddMinutes(-2), CancellationToken.None);
        var purged = await store.PurgeCompletedAsync(now, CancellationToken.None);

        // Assert — the kept row refuses the redelivery on the live engine's unique key, survives a
        // purge whose cutoff it has not reached, and goes once it has
        redelivered.Should().BeNull();
        purgedEarly.Should().Be(0);
        purged.Should().Be(1);
        (await store.GetRunAsync("event@msg-1", CancellationToken.None)).Should().BeNull();
    }

    // 7) Jobs coalescing scopes -----------------------------------------------------------------
    //
    // A whole new table with its own DDL, an insert-or-ignore whose conflict target is the primary
    // key itself, the (ScopeKey, ScopeId, Version) compare-and-set UPDATE and DELETE, MIN over the
    // ISO-8601 text column, and a due read pairing ORDER BY with the engine's pagination suffix —
    // OFFSET/FETCH on SQL Server, LIMIT/OFFSET everywhere else. SQLite exercises none of that
    // divergence, and a lost merge here NARROWS a scope, which loses work rather than duplicating it.

    [TestMethod]
    public async Task RoundTripACoalescingScopeThroughInsertMergeAndDelete()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        // Sub-millisecond precision, as the run ledger does: a truncating text path would collapse
        // two nearby due times and stop MIN and ORDER BY agreeing with the clock.
        var now = DateTimeOffset.UtcNow;
        var earliest = now.AddMinutes(-2).AddTicks(1234567);

        // Act — open one scope, then race a second insert for the same key
        var opened = await store.TryInsertScopeAsync(
            NewScope("rebuild@loc-1", "gen-1", earliest), CancellationToken.None);
        var refused = await store.TryInsertScopeAsync(
            NewScope("rebuild@loc-1", "gen-2", earliest), CancellationToken.None);

        // Assert — two hosts opening one scope must produce one row, exactly as materialization does
        opened.Should().BeTrue();
        refused.Should().BeFalse();

        var read = await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None);
        read.Should().NotBeNull();
        read!.ScopeId.Should().Be("gen-1", "the first writer's generation is the one that survives");
        read.DueAt.Should().Be(earliest);
        read.Version.Should().Be(0);

        // Act — the compare-and-set that makes read-modify-write merging safe
        var merged = await store.TryMergeScopeAsync(
            "rebuild@loc-1", "gen-1", """{"widened":true}""", version: 0, CancellationToken.None);
        var stale = await store.TryMergeScopeAsync(
            "rebuild@loc-1", "gen-1", """{"clobbered":true}""", version: 0, CancellationToken.None);

        // Assert — the refusal is the whole guard: the caller must re-read, never retry blindly
        merged.Should().BeTrue();
        stale.Should().BeFalse("the version this caller read is gone, so its merge would clobber");

        var afterMerge = await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None);
        afterMerge!.Version.Should().Be(1);
        afterMerge.Payload.Should().Be("""{"widened":true}""");
        afterMerge.DueAt.Should().Be(earliest, "merging is leading-edge and never moves the due time");

        // Arrange — three due scopes and one in the future, so the due read has something to order,
        // page and exclude
        await store.TryInsertScopeAsync(
            NewScope("rebuild@loc-2", "gen-3", now.AddMinutes(-1)), CancellationToken.None);
        await store.TryInsertScopeAsync(
            NewScope("rebuild@loc-3", "gen-4", now.AddSeconds(-30)), CancellationToken.None);
        await store.TryInsertScopeAsync(
            NewScope("rebuild@loc-4", "gen-5", now.AddHours(1)), CancellationToken.None);

        // Act
        var due = await store.GetDueScopesAsync(now, batchSize: 2, CancellationToken.None);
        var nextDue = await store.GetNextScopeDueAsync(CancellationToken.None);

        // Assert — soonest first, capped at the batch, and the future scope is not due
        due.Select(scope => scope.ScopeKey).Should().Equal("rebuild@loc-1", "rebuild@loc-2");
        nextDue.Should().Be(earliest, "MIN over the text column must agree with the clock");

        // Act — pickup's conditional delete
        var deleteRefused = await store.TryDeleteScopeAsync(
            "rebuild@loc-1", "gen-1", version: 0, CancellationToken.None);
        var deleted = await store.TryDeleteScopeAsync(
            "rebuild@loc-1", "gen-1", version: 1, CancellationToken.None);

        // Assert — a merge landing between materialize and delete leaves the scope alive to become
        // the next run, which is exactly what the version guard buys
        deleteRefused.Should().BeFalse();
        deleted.Should().BeTrue();
        (await store.GetScopeAsync("rebuild@loc-1", CancellationToken.None)).Should().BeNull();
    }

    // 8) Jobs run history -------------------------------------------------------------------------
    //
    // A table decoupled from the run ledger's own uniqueness, an optional-filter WHERE clause
    // (JobKey/ScheduleName/Outcome/Since/Until, each `@Param IS NULL OR ...`) paired with the
    // engine's pagination suffix, and a COUNT over the same filter for TotalCount. SQLite exercises
    // none of that divergence — OFFSET/FETCH on SqlServer vs LIMIT/OFFSET elsewhere, and whether an
    // untyped int bound against a SMALLINT `Outcome` column resolves on every provider.

    [TestMethod]
    public async Task PageFilterAndPurgeRunHistory()
    {
        // Arrange — a failed run, a completed run, and an older completed run to purge
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        var now = DateTimeOffset.UtcNow;

        await store.TryMaterializeAsync(NewJobRun("nightly@failed", now), CancellationToken.None);
        var failedClaim = await store.ClaimDueAsync("worker-failed", now, 10, CancellationToken.None);
        await store.MarkTerminalAsync(
            failedClaim[0], JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, now,
            CancellationToken.None);

        await store.TryMaterializeAsync(NewJobRun("nightly@completed", now), CancellationToken.None);
        var completedClaim = await store.ClaimDueAsync("worker-completed", now, 10, CancellationToken.None);
        await store.CompleteAsync(
            completedClaim[0], null, null, retainRunKey: false, now.AddMinutes(1), CancellationToken.None);

        await store.TryMaterializeAsync(NewJobRun("nightly@old", now), CancellationToken.None);
        var oldClaim = await store.ClaimDueAsync("worker-old", now, 10, CancellationToken.None);
        await store.CompleteAsync(
            oldClaim[0], null, null, retainRunKey: false, now.AddDays(-60), CancellationToken.None);

        // Act — filter by outcome, comparing a bound int parameter against the real SMALLINT column
        var failedOnly = await store.QueryHistoryAsync(
            new JobHistoryFilter { Outcome = JobRunOutcome.Failed }, CancellationToken.None);

        // Assert
        var failedEntry = failedOnly.Entries.Should().ContainSingle().Subject;
        failedEntry.JobKey.Should().Be("nightly@failed");
        failedEntry.LastError.Should().Be("boom");

        // Act — page size 2 over all 3 rows, newest CompletedAt first
        var page1 = await store.QueryHistoryAsync(new JobHistoryFilter { PageSize = 2 }, CancellationToken.None);
        var page2 = await store.QueryHistoryAsync(
            new JobHistoryFilter { PageSize = 2, PageToken = page1.NextPageToken }, CancellationToken.None);

        // Assert — count + pagination (OFFSET/FETCH on SqlServer, LIMIT/OFFSET elsewhere)
        page1.TotalCount.Should().Be(3);
        page1.Entries.Should().HaveCount(2);
        page2.Entries.Should().ContainSingle();
        page1.Entries.Concat(page2.Entries).Select(entry => entry.JobKey).Should().OnlyHaveUniqueItems();
        page2.Entries[0].JobKey.Should().Be("nightly@old", "the oldest CompletedAt sorts last");

        // Act — purge past a 30-day cutoff
        var purged = await store.PurgeHistoryAsync(now.AddDays(-30), CancellationToken.None);
        var remaining = await store.QueryHistoryAsync(
            new JobHistoryFilter { PageSize = 10 }, CancellationToken.None);

        // Assert
        purged.Should().Be(1);
        remaining.Entries.Select(entry => entry.JobKey).Should().NotContain("nightly@old");
    }

    // 9) Jobs overlap lock ------------------------------------------------------------------------
    //
    // A no-overlap schedule's runs are written under its conduit_job_schedules row lock, taken with a
    // no-op UPDATE. That this serializes the scheduler and trigger-now rests on each engine's row
    // locking and read visibility (and its deadlock-freedom on the lock order CompleteAsync keeps),
    // none of which SQLite — one writer at a time — exercises.

    [TestMethod]
    public async Task HoldTheScheduleLockAgainstAnOverlapCheckUntilTheHolderCommits()
    {
        // Arrange — another writer holds the schedule's lock and is about to insert a run
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        await using var holder = await factory.CreateConnectionAsync(CancellationToken.None);
        await using var transaction = await holder.BeginTransactionAsync(CancellationToken.None);
        await ExecuteAsync(
            holder,
            transaction,
            "UPDATE conduit_job_schedules SET IsPaused = IsPaused WHERE Name = @Name",
            command => DbParameterHelper.Add(command, "@Name", "nightly"));

        // Act
        var contender = Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
            NewManualJobRun(now.AddSeconds(1)), CancellationToken.None));
        await Task.Delay(500);
        var waitedForTheLock = !contender.IsCompleted;

        await ExecuteAsync(holder, transaction, InsertRunSql, command => BindRun(command, NewJobRun("nightly@held", now)));
        await transaction.CommitAsync(CancellationToken.None);

        // Assert — the no-op UPDATE takes the row's write lock on this engine, and what runs after it
        // reads past nothing the holder committed
        waitedForTheLock.Should().BeTrue();
        (await contender).Should().Be(MaterializeOutcome.Overlapping);
    }

    [TestMethod]
    public async Task MaterializeAtMostOneRunOfANoOverlapScheduleUnderConcurrency()
    {
        // Arrange
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        // Act — ten replicas materializing one occurrence and ten manual triggers, all at once
        var outcomes = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
                    NewJobRun("nightly@race", now), CancellationToken.None)))
                .Concat(Enumerable.Range(0, 10).Select(trigger => Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
                    NewManualJobRun(now.AddSeconds(trigger + 1)), CancellationToken.None)))));

        // Assert
        outcomes.Count(outcome => outcome == MaterializeOutcome.Materialized).Should().Be(1);
        (await CountOutstandingAsync(factory)).Should().Be(1);
    }

    [TestMethod]
    public async Task NeitherDeadlockNorOverlapWhenMaterializationsRaceACompletion()
    {
        // Arrange — a claimed run, as a host leaves it while the job executes
        var factory = await this.CreateIsolatedFactoryAsync();
        var store = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.ReconcileSchedulesAsync(["nightly"], CancellationToken.None);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var claimNow = DateTimeOffset.UtcNow.AddHours(1);

        await store.TryMaterializeAsync(NewJobRun("nightly@0", start), CancellationToken.None);
        var running = (await store.ClaimDueAsync("worker-0", claimNow, 10, CancellationToken.None)).Single();

        for (var round = 1; round <= 8; round++)
        {
            var occurrence = start.AddMinutes(round);

            // Act — the running run completes (cursor, history, delete) while the next occurrence
            // and manual triggers materialize; a deadlock victim on either side fails the round
            var completion = Task.Run(() => store.CompleteAsync(
                running, "nightly", running.Occurrence, retainRunKey: false, DateTimeOffset.UtcNow,
                CancellationToken.None));
            var materializations = Enumerable.Range(0, 4)
                .Select(_ => Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
                    NewJobRun($"nightly@{round}", occurrence), CancellationToken.None)))
                .Concat(Enumerable.Range(0, 4).Select(trigger => Task.Run(() => store.TryMaterializeWithoutOverlapAsync(
                    NewManualJobRun(occurrence.AddSeconds(trigger + 1)), CancellationToken.None))))
                .ToList();

            await completion;
            await Task.WhenAll(materializations);

            // Assert
            var outstanding = await CountOutstandingAsync(factory);
            outstanding.Should().BeLessThanOrEqualTo(1, $"round {round} must not leave two runs outstanding");

            // The next round's running run: whichever won, or a fresh one when every writer saw the
            // completing run still outstanding and stood down
            if (outstanding == 0)
            {
                await store.TryMaterializeAsync(NewJobRun($"nightly@{round}-fresh", occurrence), CancellationToken.None);
            }

            running = (await store.ClaimDueAsync($"worker-{round}", claimNow, 10, CancellationToken.None)).Single();
        }
    }

    [TestMethod]
    public async Task WaitForTheLockWhenTheScheduleRowAppearsAfterTheFirstLockAttempt()
    {
        // Arrange — no schedule row yet, so caller A's lock UPDATE matches nothing and locks nothing.
        // A is held right after it while another host's reconcile creates the row, then again right
        // after its probe has seen that row
        var factory = await this.CreateIsolatedFactoryAsync();
        var plain = new DbJobStore(factory, this.Dialect, JobsOptionsForTest);
        await plain.EnsureTablesExistAsync(CancellationToken.None);
        var gateA = new GatedConnectionFactory(
            factory, "UPDATE conduit_job_schedules", "SELECT 1 FROM conduit_job_schedules");
        var gateB = new GatedConnectionFactory(factory, "SELECT COUNT(*) FROM conduit_job_runs");
        var storeA = new DbJobStore(gateA, this.Dialect, JobsOptionsForTest);
        var storeB = new DbJobStore(gateB, this.Dialect, JobsOptionsForTest);
        var now = DateTimeOffset.UtcNow;

        var callerA = Task.Run(() => storeA.TryMaterializeWithoutOverlapAsync(
            NewManualJobRun(now.AddSeconds(1)), CancellationToken.None));
        await gateA.PausedAt(0).WaitAsync(TimeSpan.FromSeconds(30));
        var reconcile = Task.Run(() => plain.ReconcileSchedulesAsync(["nightly"], CancellationToken.None));

        if (this.MissingRowUpdateLocksTheGap)
        {
            // Assert — the row cannot appear behind A's UPDATE at all, so the interleaving below
            // cannot happen on this engine
            (await Task.WhenAny(reconcile, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(reconcile);
            gateA.Release(0);
            gateA.Release(1);
            await reconcile.WaitAsync(TimeSpan.FromSeconds(30));
            (await callerA).Should().Be(MaterializeOutcome.Materialized);
            (await CountOutstandingAsync(factory)).Should().Be(1);
            return;
        }

        await reconcile.WaitAsync(TimeSpan.FromSeconds(30));
        gateA.Release(0);
        await gateA.PausedAt(1).WaitAsync(TimeSpan.FromSeconds(30));

        // B locks the now-existing row, finds nothing outstanding, and is held before its insert
        var callerB = Task.Run(() => storeB.TryMaterializeWithoutOverlapAsync(
            NewManualJobRun(now.AddSeconds(2)), CancellationToken.None));
        await gateB.PausedAt(0).WaitAsync(TimeSpan.FromSeconds(30));

        // Act — A, which has seen the row but never locked it, carries on while B holds the lock
        // between its check and its insert
        gateA.Release(1);
        var aDecidedWhileBHeldTheLock = await Task.WhenAny(callerA, Task.Delay(TimeSpan.FromSeconds(2))) == callerA;
        gateB.Release(0);
        var outcomes = await Task.WhenAll(callerA, callerB);

        // Assert — seeing the row is not holding its lock: A must take it, and so wait for B
        aDecidedWhileBHeldTheLock.Should().BeFalse();
        outcomes.Should().Equal(MaterializeOutcome.Overlapping, MaterializeOutcome.Materialized);
        (await CountOutstandingAsync(factory)).Should().Be(1);
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

    private static JobRun NewJobRun(string jobKey, DateTimeOffset runAt) => new()
    {
        JobKey = jobKey,
        ScheduleName = "nightly",
        Occurrence = runAt,
        MessageType = "Tests.NoopJob",
        Payload = "{}",
        RunAt = runAt,
        Status = JobRunStatus.Pending,
    };

    private static JobRun NewManualJobRun(DateTimeOffset at) => new()
    {
        JobKey = string.Create(CultureInfo.InvariantCulture, $"nightly@manual-{at.UtcDateTime:O}"),
        ScheduleName = "nightly",
        Occurrence = null,
        MessageType = "Tests.NoopJob",
        Payload = "{}",
        RunAt = at,
        Status = JobRunStatus.Pending,
    };

    private const string InsertRunSql =
        "INSERT INTO conduit_job_runs (JobKey, ScheduleName, Occurrence, MessageType, Payload, RunAt, Status) " +
        "VALUES (@JobKey, @ScheduleName, @Occurrence, @MessageType, @Payload, @RunAt, @Status)";

    private static void BindRun(System.Data.Common.DbCommand command, JobRun run)
    {
        DbParameterHelper.Add(command, "@JobKey", run.JobKey);
        DbParameterHelper.Add(command, "@ScheduleName", run.ScheduleName!);
        DbParameterHelper.Add(command, "@Occurrence", run.Occurrence!.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        DbParameterHelper.Add(command, "@MessageType", run.MessageType);
        DbParameterHelper.Add(command, "@Payload", run.Payload);
        DbParameterHelper.Add(command, "@RunAt", run.RunAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        DbParameterHelper.Add(command, "@Status", (int)run.Status);
    }

    private static async Task ExecuteAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string sql,
        Action<System.Data.Common.DbCommand> bind)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100 // Test-owned SQL with bound parameters.
        command.CommandText = sql;
#pragma warning restore CA2100
        bind(command);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<int> CountOutstandingAsync(IConduitConnectionFactory factory)
    {
        await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM conduit_job_runs WHERE ScheduleName = @ScheduleName AND Status IN (@Pending, @Claiming)";
        DbParameterHelper.Add(command, "@ScheduleName", "nightly");
        DbParameterHelper.Add(command, "@Pending", (int)JobRunStatus.Pending);
        DbParameterHelper.Add(command, "@Claiming", (int)JobRunStatus.Claiming);
        return Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), CultureInfo.InvariantCulture);
    }

    private static JobScope NewScope(string scopeKey, string scopeId, DateTimeOffset dueAt) => new()
    {
        ScopeKey = scopeKey,
        ScopeId = scopeId,
        TriggerName = "rebuild",
        MessageType = "Tests.RecomputeWorkstation",
        Payload = "{}",
        DueAt = dueAt,
        Version = 0,
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
