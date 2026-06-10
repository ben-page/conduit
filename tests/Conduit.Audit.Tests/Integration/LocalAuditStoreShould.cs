using System.Text.Json;
using AwesomeAssertions;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// Functional round-trip tests for <see cref="DbAuditStore"/> against real SQLite. These exercise the
/// audit module's composed SQL (DDL, insert, select-by-message-id, delete-expired) end to end, which
/// is where the engine-specific dialect coverage now lives after the audit dialects were folded into
/// the shared <see cref="Conduit.Persistence.ISqlDialect"/>.
/// </summary>
[TestClass]
public sealed class LocalAuditStoreShould
{
    [TestMethod]
    public async Task SaveAndRetrieveAuditRecord()
    {
        // Arrange
        await using var factory = new StubConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());

        await store.EnsureTableExistsAsync(CancellationToken.None);

        var now = DateTime.UtcNow;
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
                new DispatchInvoked("Orders.Commands.PlaceOrder", "Command"),
                new HandlerStarted("PlaceOrderHandler"),
                new HandlerCompleted(49.0),
                new DispatchCompleted(55.0),
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
        await using var factory = new StubConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());

        await store.EnsureTableExistsAsync(CancellationToken.None);

        var oldTimestamp = DateTime.UtcNow.AddDays(-10);
        var recentTimestamp = DateTime.UtcNow;

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
                new DispatchInvoked("Test.Commands.OldCommand", "Command"),
                new DispatchCompleted(10.0),
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
                new DispatchInvoked("Test.Commands.RecentCommand", "Command"),
                new DispatchCompleted(5.0),
            ],
        };

        await store.SaveBatchAsync([oldRecord, recentRecord], CancellationToken.None);

        // Act
        var cutoff = DateTime.UtcNow.AddDays(-1);
        await store.PurgeExpiredAsync(cutoff, CancellationToken.None);

        // Assert
        var oldResult = await store.GetByMessageIdAsync("msg-old", CancellationToken.None);
        oldResult.Should().BeNull();

        var recentResult = await store.GetByMessageIdAsync("msg-recent", CancellationToken.None);
        recentResult.Should().NotBeNull();
        recentResult!.MessageId.Should().Be("msg-recent");
    }

    [TestMethod]
    public async Task RoundTripTimestampAsUtc()
    {
        // Arrange
        await using var factory = new StubConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());

        await store.EnsureTableExistsAsync(CancellationToken.None);

        var timestamp = new DateTime(2026, 6, 8, 13, 14, 15, 678, DateTimeKind.Utc).AddTicks(9012);
        var record = new DispatchAuditRecord
        {
            MessageId = "msg-utc",
            ConversationId = "conv-utc",
            MessageType = "Test.Commands.UtcCommand",
            MessageKind = "Command",
            ServiceName = "TestService",
            Timestamp = timestamp,
            Observations = [new DispatchInvoked("Test.Commands.UtcCommand", "Command")],
        };

        // Act
        await store.SaveBatchAsync([record], CancellationToken.None);
        var retrieved = await store.GetByMessageIdAsync("msg-utc", CancellationToken.None);

        // Assert
        retrieved.Should().NotBeNull();
        retrieved!.Timestamp.Kind.Should().Be(DateTimeKind.Utc);
        retrieved.Timestamp.Should().Be(timestamp);
    }

    [TestMethod]
    public async Task ReturnNullForNonExistentMessageId()
    {
        // Arrange
        await using var factory = new StubConnectionFactory();
        var store = new DbAuditStore(factory, new SqliteDialect());

        await store.EnsureTableExistsAsync(CancellationToken.None);

        // Act
        var result = await store.GetByMessageIdAsync("does-not-exist", CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }
}
