using System.Globalization;
using AwesomeAssertions;
using Conduit.Durability.Outbox;
using Conduit.Persistence.Sqlite;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Durability.Tests.Support;
using Conduit.TestUtils;
using Conduit.Durability.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

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
    public async Task CreateTheIndexesItsQueriesUse()
    {
        // Arrange — table and indexes already created in SetUp; a second ensure must leave them as they are.
        await this.store.EnsureTableExistsAsync(CancellationToken.None);

        // Act
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = """
            SELECT list.name, info.name
            FROM pragma_index_list('conduit_outbox') AS list, pragma_index_info(list.name) AS info
            WHERE list.origin = 'c'
            ORDER BY list.name, info.seqno
            """;
        var columnsByIndex = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var index = reader.GetString(0);
                if (!columnsByIndex.TryGetValue(index, out var columns))
                {
                    columns = [];
                    columnsByIndex[index] = columns;
                }

                columns.Add(reader.GetString(1));
            }
        }

        // Assert — the pending claim (and the stale-claim and Failed lookups, by its Status prefix), the read-back
        // by claim token, the flush's claim by message id, and the Sent retention cleanup.
        columnsByIndex.Select(pair => $"{pair.Key} ({string.Join(", ", pair.Value)})").Should().Equal(
            "ix_conduit_outbox_claim (ClaimToken)",
            "ix_conduit_outbox_message_id (MessageId)",
            "ix_conduit_outbox_status_next_attempt (Status, NextAttemptAt)",
            "ix_conduit_outbox_status_sent (Status, SentAt)");
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
    public async Task NotResolveARowItsOwnClaimAlreadyResolved()
    {
        // Arrange — the claim marks its entry sent, which leaves the claim's token on the row
        await this.SaveEntryAsync("msg-resolved");
        var claimed = (await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None)).Single();
        (await this.store.MarkSentAsync(claimed.Id, claimed.ClaimToken!, CancellationToken.None)).Should().BeTrue();

        // Act — a second outcome under the same token, as a failure after the mark-sent would attempt
        var retried = await this.store.IncrementRetryAsync(claimed.Id, claimed.ClaimToken!, TimeSpan.Zero, CancellationToken.None);
        var markedFailed = await this.store.MarkFailedAsync(claimed.Id, claimed.ClaimToken!, CancellationToken.None);

        // Assert — the row stays Sent: not released to be delivered again, not marked failed
        retried.Should().BeFalse();
        markedFailed.Should().BeFalse();
        (await this.ReadRowAsync(claimed.Id)).Should().Be((OutboxEntryStatus.Sent, 0, claimed.ClaimToken));
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
        await this.SetSentAtAsync(claimed[0].Id, DateTime.UtcNow.AddDays(-8));

        // Act — delete entries sent more than 7 days ago
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
        await this.SetFailedAtAsync(claimed[0].Id, DateTime.UtcNow.AddDays(-8));

        var connection = this.connectionFactory.Connection;

        // Act — delete completed (Sent) entries older than 7 days
        await this.store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — the Failed row survives
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conduit_outbox WHERE MessageId = 'msg-old-failed'";
        var survived = await command.ExecuteScalarAsync();
        survived.Should().Be(1L);

        // Act — delete Failed entries whose last attempt failed more than 7 days ago
        await this.store.DeleteFailedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — now it is gone
        var gone = await command.ExecuteScalarAsync();
        gone.Should().Be(0L);
    }

    [TestMethod]
    public async Task KeepASentEntryForItsRetentionFromWhenItWasSent()
    {
        // Arrange — written eight days ago, sent only now (the broker was down for a week)
        await this.SaveEntryAsync("msg-late-sent", DateTime.UtcNow.AddDays(-8));
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await this.store.MarkSentAsync(claimed[0].Id, claimed[0].ClaimToken!, CancellationToken.None);

        // Act
        await this.store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert
        (await this.CountAsync("msg-late-sent")).Should().Be(1L, "it was sent less than seven days ago");
    }

    [TestMethod]
    public async Task KeepAFailedEntryForItsRetentionFromItsLastAttempt()
    {
        // Arrange — written eight days ago, its last attempt failed only now
        await this.SaveEntryAsync("msg-late-failed", DateTime.UtcNow.AddDays(-8));
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        await this.store.MarkFailedAsync(claimed[0].Id, claimed[0].ClaimToken!, CancellationToken.None);

        // Act
        await this.store.DeleteFailedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert
        (await this.CountAsync("msg-late-failed")).Should().Be(1L, "its last attempt failed less than seven days ago");
    }

    [TestMethod]
    public async Task RecordWhenAnEntryFailed_AndForgetItWhenTheEntryIsRetried()
    {
        // Arrange
        await this.SaveEntryAsync("msg-failed");
        var claimed = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var before = DateTime.UtcNow;

        // Act
        await this.store.MarkFailedAsync(claimed[0].Id, claimed[0].ClaimToken!, CancellationToken.None);
        var failedAt = await this.ReadFailedAtAsync(claimed[0].Id);
        await this.store.RetryFailedAsync(claimed[0].Id, CancellationToken.None);
        var failedAtAfterRetry = await this.ReadFailedAtAsync(claimed[0].Id);

        // Assert
        failedAt.Should().NotBeNull();
        DateTime.Parse(failedAt!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            .Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        failedAtAfterRetry.Should().BeNull("a retried entry is pending again");
    }

    [TestMethod]
    public async Task ClaimByTheInjectedClock()
    {
        // Arrange — the store's clock stands at the entry's creation, years away from the machine's
        var createdAt = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(createdAt);
        var store = new DbConnectionOutboxStore(this.connectionFactory, new SqliteDialect(), options: null, clock);
        await this.SaveEntryAsync("msg-clock", createdAt.UtcDateTime);

        // Act — inside the grace period, then past it
        var withinGrace = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.FromSeconds(10), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        var pastGrace = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.FromSeconds(10), CancellationToken.None);

        // Act — the claim held a tick short of the five-minute claim timeout, then outlasting it
        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromTicks(1));
        var whileHeld = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        clock.Advance(TimeSpan.FromTicks(1));
        var onceStale = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Act — a failed send backs off for a minute
        await store.IncrementRetryAsync(onceStale[0].Id, onceStale[0].ClaimToken!, TimeSpan.FromMinutes(1), CancellationToken.None);
        var backingOff = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var dueAgain = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);

        // Assert
        withinGrace.Should().BeEmpty();
        pastGrace.Should().ContainSingle();
        whileHeld.Should().BeEmpty();
        onceStale.Should().ContainSingle();
        backingOff.Should().BeEmpty();
        dueAgain.Should().ContainSingle();
    }

    [TestMethod]
    public async Task StampAndRetainByTheInjectedClock()
    {
        // Arrange — one entry sent and one failed, at the store's clock
        var now = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new DbConnectionOutboxStore(this.connectionFactory, new SqliteDialect(), options: null, clock);
        await this.SaveEntryAsync("msg-sent", now.UtcDateTime);
        await this.SaveEntryAsync("msg-failed", now.UtcDateTime);
        var claimed = await store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        var sent = claimed.Single(entry => entry.MessageId == "msg-sent");
        var failed = claimed.Single(entry => entry.MessageId == "msg-failed");
        await store.MarkSentAsync(sent.Id, sent.ClaimToken!, CancellationToken.None);
        await store.MarkFailedAsync(failed.Id, failed.ClaimToken!, CancellationToken.None);
        var failedAt = await this.ReadFailedAtAsync(failed.Id);

        // Act — the seven-day retentions, a tick short of a week on, then a week on
        clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromTicks(1));
        await store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);
        await store.DeleteFailedAsync(TimeSpan.FromDays(7), CancellationToken.None);
        var keptForTheWeek = (await this.CountAsync("msg-sent"), await this.CountAsync("msg-failed"));
        clock.Advance(TimeSpan.FromTicks(1));
        await store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);
        await store.DeleteFailedAsync(TimeSpan.FromDays(7), CancellationToken.None);
        var keptAfterTheWeek = (await this.CountAsync("msg-sent"), await this.CountAsync("msg-failed"));

        // Assert
        failedAt.Should().Be(now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        keptForTheWeek.Should().Be((1L, 1L));
        keptAfterTheWeek.Should().Be((0L, 0L));
    }

    [TestMethod]
    public async Task WriteALocalCreatedAtAsItsUtcInstant()
    {
        // Arrange — an entry a caller stamped with this host's local time
        var createdAt = DateTime.Now.AddMinutes(-1);

        // Act
        await this.SaveEntryAsync("msg-local", createdAt);

        // Assert — stored as the UTC instant, as every timestamp the store writes itself is: the claim compares them as
        // text, so a local offset would make the entry look hours older or younger than it is
        var expected = createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "SELECT CreatedAt, NextAttemptAt FROM conduit_outbox WHERE MessageId = 'msg-local'";
        using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(expected);
        reader.GetString(1).Should().Be(expected);
    }

    private async Task SaveEntryAsync(string messageId, DateTime? createdAt = null)
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
                    CreatedAt = createdAt ?? DateTime.UtcNow.AddMinutes(-1),
                },
            ],
            this.connectionFactory.GetCurrentTransaction()!,
            CancellationToken.None);
        await this.connectionFactory.CommitAsync();
    }

    private async Task SetSentAtAsync(long id, DateTime sentAt)
    {
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "UPDATE conduit_outbox SET SentAt = @SentAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@SentAt", sentAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetFailedAtAsync(long id, DateTime failedAt)
    {
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "UPDATE conduit_outbox SET FailedAt = @FailedAt WHERE Id = @Id";
        command.Parameters.AddWithValue("@FailedAt", failedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadFailedAtAsync(long id)
    {
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "SELECT FailedAt FROM conduit_outbox WHERE Id = @Id";
        command.Parameters.AddWithValue("@Id", id);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<long> CountAsync(string messageId)
    {
        using var command = this.connectionFactory.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conduit_outbox WHERE MessageId = @MessageId";
        command.Parameters.AddWithValue("@MessageId", messageId);
        return (long)(await command.ExecuteScalarAsync())!;
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
