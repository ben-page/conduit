using System.Text.Json;
using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Runtime;
using Microsoft.Data.Sqlite;

namespace Conduit.Audit.Tests.Integration;

[TestClass]
public sealed class LocalAuditStoreShould
{
    [TestMethod]
    public async Task SaveAndRetrieveAuditRecord()
    {
        // Arrange
        var dbName = $"test_save_{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var factory = new TestConnectionFactory(connectionString);
        var dialect = new SqliteAuditDialect();
        var store = new DbAuditStore(factory, dialect);

        await store.EnsureTableExistsAsync(CancellationToken.None);

        var now = DateTimeOffset.UtcNow;
        var record = new DispatchAuditRecord
        {
            MessageId = "msg-001",
            ConversationId = "conv-001",
            CausationId = "cause-001",
            MessageType = "Orders.Commands.PlaceOrder",
            MessageKind = "Command",
            ServiceName = "OrderService",
            Payload = JsonSerializer.SerializeToElement(new { orderId = 42, amount = 99.95 }),
            Properties = new Dictionary<string, object?> { ["tenant"] = "acme", ["userId"] = "user-1" },
            Timestamp = now,
            Observations =
            [
                new DispatchInvoked(now, "Orders.Commands.PlaceOrder", "Command"),
                new HandlerStarted(now.AddMilliseconds(1), "PlaceOrderHandler"),
                new HandlerCompleted(now.AddMilliseconds(50), 49.0),
                new DispatchCompleted(now.AddMilliseconds(55), 55.0),
            ],
        };

        // Act
        await store.SaveBatchAsync([record], CancellationToken.None);
        var retrieved = await store.GetByMessageIdAsync("msg-001", CancellationToken.None);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.MessageId.Should().Be("msg-001");
        retrieved.ConversationId.Should().Be("conv-001");
        retrieved.CausationId.Should().Be("cause-001");
        retrieved.MessageType.Should().Be("Orders.Commands.PlaceOrder");
        retrieved.MessageKind.Should().Be("Command");
        retrieved.ServiceName.Should().Be("OrderService");

        retrieved.Payload.Should().NotBeNull();
        retrieved.Payload!.Value.GetProperty("orderId").GetInt32().Should().Be(42);

        retrieved.Properties.Should().NotBeNull();
        retrieved.Properties!.Should().ContainKey("tenant");
        retrieved.Properties!.Should().ContainKey("userId");

        retrieved.Observations.Should().HaveCount(4);
        retrieved.Observations[0].Should().BeOfType<DispatchInvoked>();
        retrieved.Observations[1].Should().BeOfType<HandlerStarted>();
        retrieved.Observations[2].Should().BeOfType<HandlerCompleted>();
        retrieved.Observations[3].Should().BeOfType<DispatchCompleted>();

        var dispatchInvoked = (DispatchInvoked)retrieved.Observations[0];
        dispatchInvoked.MessageType.Should().Be("Orders.Commands.PlaceOrder");
        dispatchInvoked.MessageKind.Should().Be("Command");

        var handlerCompleted = (HandlerCompleted)retrieved.Observations[2];
        handlerCompleted.DurationMs.Should().Be(49.0);
    }

    [TestMethod]
    public async Task PurgeExpiredRecords()
    {
        // Arrange
        var dbName = $"test_purge_{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var factory = new TestConnectionFactory(connectionString);
        var dialect = new SqliteAuditDialect();
        var store = new DbAuditStore(factory, dialect);

        await store.EnsureTableExistsAsync(CancellationToken.None);

        var oldTimestamp = DateTimeOffset.UtcNow.AddDays(-10);
        var recentTimestamp = DateTimeOffset.UtcNow;

        var oldRecord = new DispatchAuditRecord
        {
            MessageId = "msg-old",
            ConversationId = "conv-old",
            MessageType = "Test.Commands.OldCommand",
            MessageKind = "Command",
            ServiceName = "TestService",
            Timestamp = oldTimestamp,
            Observations =
            [
                new DispatchInvoked(oldTimestamp, "Test.Commands.OldCommand", "Command"),
                new DispatchCompleted(oldTimestamp.AddMilliseconds(10), 10.0),
            ],
        };

        var recentRecord = new DispatchAuditRecord
        {
            MessageId = "msg-recent",
            ConversationId = "conv-recent",
            MessageType = "Test.Commands.RecentCommand",
            MessageKind = "Command",
            ServiceName = "TestService",
            Timestamp = recentTimestamp,
            Observations =
            [
                new DispatchInvoked(recentTimestamp, "Test.Commands.RecentCommand", "Command"),
                new DispatchCompleted(recentTimestamp.AddMilliseconds(5), 5.0),
            ],
        };

        await store.SaveBatchAsync([oldRecord, recentRecord], CancellationToken.None);

        // Act
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);
        await store.PurgeExpiredAsync(cutoff, CancellationToken.None);

        // Assert
        var oldResult = await store.GetByMessageIdAsync("msg-old", CancellationToken.None);
        oldResult.Should().BeNull();

        var recentResult = await store.GetByMessageIdAsync("msg-recent", CancellationToken.None);
        recentResult.Should().NotBeNull();
        recentResult!.MessageId.Should().Be("msg-recent");
    }

    [TestMethod]
    public async Task ReturnNullForNonExistentMessageId()
    {
        // Arrange
        var dbName = $"test_null_{Guid.NewGuid():N}";
        var connectionString = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        using var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync();
        var factory = new TestConnectionFactory(connectionString);
        var dialect = new SqliteAuditDialect();
        var store = new DbAuditStore(factory, dialect);

        await store.EnsureTableExistsAsync(CancellationToken.None);

        // Act
        var result = await store.GetByMessageIdAsync("does-not-exist", CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    /// <summary>
    /// Test connection factory that creates new connections from a shared-cache connection string.
    /// The shared-cache in-memory database survives across connections as long as at least one
    /// connection remains open (the keep-alive connection in each test).
    /// </summary>
    private sealed class TestConnectionFactory(string connectionString) : IAuditConnectionFactory
    {
        public System.Data.Common.DbConnection CreateConnection() =>
            new SqliteConnection(connectionString);
    }
}
