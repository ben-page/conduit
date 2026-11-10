using System.Globalization;
using AwesomeAssertions;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.TestUtils;
using Conduit.Durability.Configuration;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Tests.Unit;

[TestClass]
public sealed class DbConnectionOutboxStoreShould : IAsyncDisposable
{
    private StubConnectionFactory connectionFactory = null!;
    private DbConnectionOutboxStore store = null!;

    [TestInitialize]
    public async Task SetUp()
    {
        this.connectionFactory = new StubConnectionFactory();
        this.store = new DbConnectionOutboxStore(this.connectionFactory, new SqliteDialect());
        await this.store.EnsureTableExistsAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await this.connectionFactory.DisposeAsync();
    }

    [TestMethod]
    public async Task CreateTableOnEnsure()
    {
        // Arrange — table already created in SetUp. Query sqlite_master to confirm.
        var connection = this.connectionFactory.Connection;

        // Act
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='conduit_outbox'";
        var result = await command.ExecuteScalarAsync();

        // Assert
        result.Should().Be("conduit_outbox");
    }

    [TestMethod]
    public async Task SaveAndClaimEntries()
    {
        // Arrange
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-1",
                ConversationId = "conv-1",
                CausationId = null,
                Type = "Orders.Commands.CreateOrder",
                Payload = """{"orderId":"abc"}""",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            },
            new()
            {
                MessageId = "msg-2",
                ConversationId = "conv-1",
                CausationId = "msg-1",
                Type = "Orders.Commands.ShipOrder",
                Payload = """{"orderId":"abc","address":"123 Main St"}""",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            },
        };

        // Act
        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        // Claim with grace period of zero so entries are eligible immediately
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert
        claimed.Should().HaveCount(2);
        claimed.Should().ContainSingle(e => e.MessageId == "msg-1");
        claimed.Should().ContainSingle(e => e.MessageId == "msg-2");

        var first = claimed.Single(e => e.MessageId == "msg-1");
        first.ConversationId.Should().Be("conv-1");
        first.CausationId.Should().BeNull();
        first.Type.Should().Be("Orders.Commands.CreateOrder");
        first.Payload.Should().Be("""{"orderId":"abc"}""");

        // Claiming a pending entry atomically transitions it to Claiming — that exclusive
        // transition is what prevents two workers from dispatching the same row.
        first.Status.Should().Be(OutboxEntryStatus.Claiming);
        first.RetryCount.Should().Be(0);

        var second = claimed.Single(e => e.MessageId == "msg-2");
        second.CausationId.Should().Be("msg-1");
    }

    [TestMethod]
    public async Task SaveAndClaimWithoutThePropertiesColumnWhenNoneAreListed()
    {
        // Arrange — a table without the Properties column, and a host that lists no properties: the store must
        // neither write nor read the column, so that host behaves exactly as before.
        await using var legacyFactory = new StubConnectionFactory();
        using (var create = legacyFactory.Connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE conduit_outbox (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    MessageId TEXT NOT NULL,
                    ConversationId TEXT NOT NULL,
                    CausationId TEXT,
                    Type TEXT NOT NULL,
                    Payload TEXT NOT NULL,
                    Status INTEGER NOT NULL DEFAULT 0,
                    CreatedAt TEXT NOT NULL,
                    SentAt TEXT,
                    RetryCount INTEGER NOT NULL DEFAULT 0,
                    NextAttemptAt TEXT NOT NULL,
                    ClaimedAt TEXT,
                    ClaimToken TEXT)
                """;
            await create.ExecuteNonQueryAsync();
        }

        var legacyStore = new DbConnectionOutboxStore(
            legacyFactory, new SqliteDialect(), Options.Create(new OutboxOptions()));
        await legacyFactory.BeginTransactionAsync();

        // Act
        await legacyStore.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = "msg-legacy",
                    ConversationId = "conv-1",
                    Type = "Orders.Commands.CreateOrder",
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            legacyFactory.GetCurrentTransaction()!,
            CancellationToken.None);
        await legacyFactory.CommitAsync();
        var claimed = await legacyStore.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert
        claimed.Should().ContainSingle().Which.Properties.Should().BeNull();
    }

    [TestMethod]
    public async Task NotClaimEntriesWithinGracePeriod()
    {
        // Arrange — entry with CreatedAt = now (inside grace period)
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-recent",
                ConversationId = "conv-1",
                Type = "Orders.Commands.CreateOrder",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow,
            },
        };

        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        // Act — 10 second grace period, entry was created just now
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.FromSeconds(10), CancellationToken.None);

        // Assert
        claimed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task MarkEntryAsSent()
    {
        // Arrange
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-sent",
                ConversationId = "conv-1",
                Type = "Orders.Commands.CreateOrder",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            },
        };

        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        claimed.Should().HaveCount(1);
        var entryId = claimed[0].Id;

        // Act
        await this.store.MarkSentAsync(entryId, claimed[0].ClaimToken!, CancellationToken.None);

        // Assert — entry no longer claimable
        var claimedAgain = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        claimedAgain.Should().BeEmpty();
    }

    [TestMethod]
    public async Task MarkEntryAsFailed()
    {
        // Arrange
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-fail",
                ConversationId = "conv-1",
                Type = "Orders.Commands.CreateOrder",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            },
        };

        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var entryId = claimed[0].Id;

        // Act
        await this.store.MarkFailedAsync(entryId, claimed[0].ClaimToken!, CancellationToken.None);

        // Assert — entry no longer claimable
        var claimedAgain = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        claimedAgain.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IncrementRetryCount()
    {
        // Arrange
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-retry",
                ConversationId = "conv-1",
                Type = "Orders.Commands.CreateOrder",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            },
        };

        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var entryId = claimed[0].Id;

        // Act — no delay, so the entry is due again at once
        await this.store.IncrementRetryAsync(entryId, claimed[0].ClaimToken!, TimeSpan.Zero, CancellationToken.None);

        // Assert — IncrementRetry releases the claim back to Pending, so the entry is claimable
        // again; re-claiming it transitions it to Claiming with the incremented retry count.
        var claimedAgain = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        claimedAgain.Should().HaveCount(1);
        claimedAgain[0].RetryCount.Should().Be(1);
        claimedAgain[0].Status.Should().Be(OutboxEntryStatus.Claiming);
    }

    [TestMethod]
    public async Task NotClaimARowBeforeItsNextAttempt()
    {
        // Arrange — two entries whose sends failed: one is due again now, the other only in an hour
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;
        await this.store.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = "msg-due",
                    ConversationId = "conv-1",
                    Type = "Orders.Commands.CreateOrder",
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
                new OutboxEntry
                {
                    MessageId = "msg-later",
                    ConversationId = "conv-1",
                    Type = "Orders.Commands.CreateOrder",
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            transaction,
            CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var due = claimed.Single(e => e.MessageId == "msg-due");
        var later = claimed.Single(e => e.MessageId == "msg-later");
        await this.store.IncrementRetryAsync(due.Id, due.ClaimToken!, TimeSpan.Zero, CancellationToken.None);
        await this.store.IncrementRetryAsync(later.Id, later.ClaimToken!, TimeSpan.FromHours(1), CancellationToken.None);

        // Act
        var reclaimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert
        reclaimed.Should().ContainSingle().Which.MessageId.Should().Be("msg-due");
    }

    [TestMethod]
    public async Task NotResolveARowWhoseClaimMovedOn()
    {
        // Arrange — worker A claims the entry and outlasts the claim timeout, so worker B re-claims it
        await this.SaveEntryAsync("msg-moved-on");
        var first = (await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None)).Single();
        await this.SetClaimedAtAsync(first.Id, DateTime.UtcNow.AddMinutes(-6));
        var second = (await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None)).Single();

        // Act — worker A resolves the entry with the token of the claim it lost, each of the three ways
        var markedSent = await this.store.MarkSentAsync(first.Id, first.ClaimToken!, CancellationToken.None);
        var retried = await this.store.IncrementRetryAsync(first.Id, first.ClaimToken!, TimeSpan.Zero, CancellationToken.None);
        var markedFailed = await this.store.MarkFailedAsync(first.Id, first.ClaimToken!, CancellationToken.None);

        // Assert — none of them touched the row: it is still worker B's claim, at the retry count B claimed it with
        markedSent.Should().BeFalse();
        retried.Should().BeFalse();
        markedFailed.Should().BeFalse();
        second.ClaimToken.Should().NotBe(first.ClaimToken);
        (await this.ReadRowAsync(first.Id)).Should().Be((OutboxEntryStatus.Claiming, 0, second.ClaimToken));
        (await this.store.MarkSentAsync(second.Id, second.ClaimToken!, CancellationToken.None)).Should().BeTrue();
    }

    [TestMethod]
    public async Task TakeOverAClaimOnlyOnceItIsOlderThanTheConfiguredClaimTimeout()
    {
        // Arrange — a host whose deliveries can run long gives its claims an hour
        var store = new DbConnectionOutboxStore(
            this.connectionFactory,
            new SqliteDialect(),
            Options.Create(new OutboxOptions { ClaimTimeout = TimeSpan.FromHours(1) }));
        await this.SaveEntryAsync("msg-slow");
        var claimed = (await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None)).Single();

        // Act — another worker claims while the claim is ten minutes old, then again once it is past the hour
        await this.SetClaimedAtAsync(claimed.Id, DateTime.UtcNow.AddMinutes(-10));
        var whileHeld = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await this.SetClaimedAtAsync(claimed.Id, DateTime.UtcNow.AddMinutes(-61));
        var onceStale = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert
        whileHeld.Should().BeEmpty();
        onceStale.Should().ContainSingle().Which.Id.Should().Be(claimed.Id);
    }

    [TestMethod]
    public async Task ClaimEntriesByMessageIds()
    {
        // Arrange
        var factory = new StubConnectionFactory();
        await using var _ = factory;
        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await factory.BeginTransactionAsync();
        var transaction = factory.GetCurrentTransaction()!;
        await store.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = "msg-1",
                    ConversationId = "conv-1",
                    Type = "TestEvent",
                    Payload = """{"value":1}""",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
                new OutboxEntry
                {
                    MessageId = "msg-2",
                    ConversationId = "conv-2",
                    Type = "TestEvent",
                    Payload = """{"value":2}""",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            transaction,
            CancellationToken.None);
        await factory.CommitAsync();

        // Act
        var claimed = await store.ClaimByMessageIdsAsync(["msg-1", "msg-2"], CancellationToken.None);

        // Assert
        claimed.Should().HaveCount(2);
        claimed[0].MessageId.Should().Be("msg-1");
        claimed[1].MessageId.Should().Be("msg-2");
        claimed[0].Id.Should().BeGreaterThan(0);
        claimed[1].Id.Should().BeGreaterThan(claimed[0].Id);
    }

    [TestMethod]
    public async Task NotClaimAlreadySentEntries()
    {
        // Arrange
        var factory = new StubConnectionFactory();
        await using var _ = factory;
        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await factory.BeginTransactionAsync();
        var transaction = factory.GetCurrentTransaction()!;
        await store.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = "msg-1",
                    ConversationId = "conv-1",
                    Type = "TestEvent",
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            transaction,
            CancellationToken.None);
        await factory.CommitAsync();

        var pending = await store.ClaimPendingAsync(10, TimeSpan.Zero, CancellationToken.None);
        await store.MarkSentAsync(pending[0].Id, pending[0].ClaimToken!, CancellationToken.None);

        // Act
        var claimed = await store.ClaimByMessageIdsAsync(["msg-1"], CancellationToken.None);

        // Assert
        claimed.Should().BeEmpty();
    }

    [TestMethod]
    public async Task NotClaimByMessageIdAnEntryTheRelayHasAlreadyAttempted()
    {
        // Arrange — a post-commit flush that runs later than the grace period finds the relay already
        // tried the entry, failed, and released it back to Pending. The relay owns its retries from here:
        // a flush attempt on top would be one attempt beyond MaxRetries that no RetryCount check sees.
        var factory = new StubConnectionFactory();
        await using var _ = factory;
        var store = new DbConnectionOutboxStore(factory, new SqliteDialect());
        await store.EnsureTableExistsAsync(CancellationToken.None);

        await factory.BeginTransactionAsync();
        var transaction = factory.GetCurrentTransaction()!;
        await store.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = "msg-1",
                    ConversationId = "conv-1",
                    Type = "TestEvent",
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            transaction,
            CancellationToken.None);
        await factory.CommitAsync();

        var relayed = await store.ClaimPendingAsync(10, TimeSpan.Zero, CancellationToken.None);
        await store.IncrementRetryAsync(relayed[0].Id, relayed[0].ClaimToken!, TimeSpan.Zero, CancellationToken.None);

        // Act
        var claimed = await store.ClaimByMessageIdsAsync(["msg-1"], CancellationToken.None);

        // Assert
        claimed.Should().BeEmpty();
        var reclaimed = await store.ClaimPendingAsync(10, TimeSpan.Zero, CancellationToken.None);
        reclaimed.Should().ContainSingle().Which.RetryCount.Should().Be(1, "the relay still retries it");
    }

    [TestMethod]
    public async Task DeleteCompletedEntriesOlderThanRetentionPeriod()
    {
        // Arrange — save an entry, mark it sent
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-old-sent",
                ConversationId = "conv-1",
                Type = "Orders.Commands.CreateOrder",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow.AddDays(-8),
            },
        };

        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await this.store.MarkSentAsync(claimed[0].Id, claimed[0].ClaimToken!, CancellationToken.None);

        // Act — delete entries older than 7 days
        await this.store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — row is gone
        var connection = this.connectionFactory.Connection;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conduit_outbox WHERE MessageId = 'msg-old-sent'";
        var count = await command.ExecuteScalarAsync();
        count.Should().Be(0L);
    }

    [TestMethod]
    public async Task DeleteCompletedLeavesFailedEntries_DeleteFailedRemovesThem()
    {
        // Arrange — save an entry, mark it failed
        await this.connectionFactory.BeginTransactionAsync();
        var transaction = this.connectionFactory.GetCurrentTransaction()!;

        var entries = new List<OutboxEntry>
        {
            new()
            {
                MessageId = "msg-old-failed",
                ConversationId = "conv-1",
                Type = "Orders.Commands.CreateOrder",
                Payload = "{}",
                CreatedAt = DateTime.UtcNow.AddDays(-8),
            },
        };

        await this.store.SaveAsync(entries, transaction, CancellationToken.None);
        await this.connectionFactory.CommitAsync();

        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await this.store.MarkFailedAsync(claimed[0].Id, claimed[0].ClaimToken!, CancellationToken.None);

        var connection = this.connectionFactory.Connection;

        // Act — delete completed (Sent) entries older than 7 days
        await this.store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — the Failed row survives
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conduit_outbox WHERE MessageId = 'msg-old-failed'";
        var survived = await command.ExecuteScalarAsync();
        survived.Should().Be(1L);

        // Act — delete Failed entries older than 7 days
        await this.store.DeleteFailedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — now it is gone
        var gone = await command.ExecuteScalarAsync();
        gone.Should().Be(0L);
    }

    private async Task SaveEntryAsync(string messageId)
    {
        await this.connectionFactory.BeginTransactionAsync();
        await this.store.SaveAsync(
            [
                new OutboxEntry
                {
                    MessageId = messageId,
                    ConversationId = "conv-1",
                    Type = "Orders.Commands.CreateOrder",
                    Payload = "{}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            this.connectionFactory.GetCurrentTransaction()!,
            CancellationToken.None);
        await this.connectionFactory.CommitAsync();
    }

    private async Task SetClaimedAtAsync(long id, DateTime claimedAt)
    {
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "UPDATE conduit_outbox SET ClaimedAt = @ClaimedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@ClaimedAt", claimedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(OutboxEntryStatus Status, int RetryCount, string? ClaimToken)> ReadRowAsync(long id)
    {
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "SELECT Status, RetryCount, ClaimToken FROM conduit_outbox WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            (OutboxEntryStatus)reader.GetInt32(0),
            reader.GetInt32(1),
            await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }
}
