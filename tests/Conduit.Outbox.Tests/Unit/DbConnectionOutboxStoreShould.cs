using AwesomeAssertions;
using Conduit.Outbox.Dialects;
using Conduit.Outbox.Runtime;
using Conduit.Outbox.Store;
using Conduit.Outbox.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Conduit.Outbox.Tests.Unit;

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
        first.Status.Should().Be(OutboxEntryStatus.Pending);
        first.RetryCount.Should().Be(0);

        var second = claimed.Single(e => e.MessageId == "msg-2");
        second.CausationId.Should().Be("msg-1");
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
        await this.store.MarkSentAsync(entryId, CancellationToken.None);

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
        await this.store.MarkFailedAsync(entryId, CancellationToken.None);

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

        // Act
        await this.store.IncrementRetryAsync(entryId, CancellationToken.None);

        // Assert — still claimable (status stays Pending), retry count incremented
        var claimedAgain = await this.store.ClaimPendingAsync(batchSize: 10, gracePeriod: TimeSpan.Zero, CancellationToken.None);
        claimedAgain.Should().HaveCount(1);
        claimedAgain[0].RetryCount.Should().Be(1);
        claimedAgain[0].Status.Should().Be(OutboxEntryStatus.Pending);
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
        await this.store.MarkSentAsync(claimed[0].Id, CancellationToken.None);

        // Act — delete entries older than 7 days
        await this.store.DeleteCompletedAsync(TimeSpan.FromDays(7), CancellationToken.None);

        // Assert — row is gone
        var connection = this.connectionFactory.Connection;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conduit_outbox WHERE MessageId = 'msg-old-sent'";
        var count = await command.ExecuteScalarAsync();
        count.Should().Be(0L);
    }
}
