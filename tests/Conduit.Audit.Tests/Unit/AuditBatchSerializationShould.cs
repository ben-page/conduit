using System.Text.Json;
using AwesomeAssertions;
using Conduit.Audit.Runtime;
using Conduit.Runtime;

namespace Conduit.Audit.Tests.Unit;

[TestClass]
public sealed class AuditBatchSerializationShould
{
    [TestMethod]
    public void SurviveJsonRoundTrip()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var record = new DispatchAuditRecord
        {
            MessageId = "msg-1",
            ConversationId = "conv-1",
            CausationId = "cause-1",
            MessageType = "Orders.CreateOrder",
            MessageKind = "Command",
            ServiceName = "OrderService",
            Timestamp = now,
            Payload = JsonSerializer.SerializeToElement(new { orderId = 42 }),
            Properties = new Dictionary<string, object?> { ["partner"] = "ACME" },
            Observations =
            [
                new DispatchInvoked(now, "Orders.CreateOrder", "Command"),
                new HandlerStarted(now, "CreateOrderHandler"),
                new RetryAttempted(now, 1, "timeout", "TimeoutException", 200.0),
                new HandlerCompleted(now, 450.0),
                new TransportSendStarted(now, "rabbitmq", "inventory"),
                new TransportSendCompleted(now, 23.0),
                new DispatchCompleted(now, 500.0),
                new DispatchFailed(now, 100.0, "boom", "InvalidOperationException"),
                new HandlerFailed(now, "handler boom", "NullReferenceException"),
                new TransportSendFailed(now, "send failed", "TimeoutException"),
                new OutboxWritten(now, 3),
                new OutboxSent(now, 42),
                new InboxDuplicate(now),
                new DeadLettered(now, "max retries", "orders-queue"),
                new CustomObservation(now, "my.metric", "custom-data"),
            ],
        };

        var batch = new AuditBatch("OrderService", "host-1", now, [record]);

        // Act
        var json = JsonSerializer.Serialize(batch, ConduitRuntime.JsonOptions);
        var deserialized = JsonSerializer.Deserialize<AuditBatch>(json, ConduitRuntime.JsonOptions);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.SourceService.Should().Be("OrderService");
        deserialized.SourceHost.Should().Be("host-1");
        deserialized.Records.Should().HaveCount(1);

        var roundTripped = deserialized.Records[0];
        roundTripped.MessageId.Should().Be("msg-1");
        roundTripped.ConversationId.Should().Be("conv-1");
        roundTripped.CausationId.Should().Be("cause-1");
        roundTripped.MessageType.Should().Be("Orders.CreateOrder");
        roundTripped.MessageKind.Should().Be("Command");
        roundTripped.ServiceName.Should().Be("OrderService");
        roundTripped.Payload.Should().NotBeNull();
        roundTripped.Payload!.Value.GetProperty("orderId").GetInt32().Should().Be(42);

        roundTripped.Observations.Should().HaveCount(15);

        // Verify each polymorphic type survives
        roundTripped.Observations[0].Should().BeOfType<DispatchInvoked>();
        var invoked = (DispatchInvoked)roundTripped.Observations[0];
        invoked.MessageType.Should().Be("Orders.CreateOrder");
        invoked.MessageKind.Should().Be("Command");

        roundTripped.Observations[1].Should().BeOfType<HandlerStarted>();
        ((HandlerStarted)roundTripped.Observations[1]).HandlerType.Should().Be("CreateOrderHandler");

        roundTripped.Observations[2].Should().BeOfType<RetryAttempted>();
        var retry = (RetryAttempted)roundTripped.Observations[2];
        retry.Attempt.Should().Be(1);
        retry.ErrorMessage.Should().Be("timeout");
        retry.ExceptionType.Should().Be("TimeoutException");
        retry.DelayMs.Should().Be(200.0);

        roundTripped.Observations[3].Should().BeOfType<HandlerCompleted>();
        ((HandlerCompleted)roundTripped.Observations[3]).DurationMs.Should().Be(450.0);

        roundTripped.Observations[4].Should().BeOfType<TransportSendStarted>();
        var sendStarted = (TransportSendStarted)roundTripped.Observations[4];
        sendStarted.Transport.Should().Be("rabbitmq");
        sendStarted.Endpoint.Should().Be("inventory");

        roundTripped.Observations[5].Should().BeOfType<TransportSendCompleted>();
        ((TransportSendCompleted)roundTripped.Observations[5]).DurationMs.Should().Be(23.0);

        roundTripped.Observations[6].Should().BeOfType<DispatchCompleted>();
        ((DispatchCompleted)roundTripped.Observations[6]).DurationMs.Should().Be(500.0);

        roundTripped.Observations[7].Should().BeOfType<DispatchFailed>();
        var failed = (DispatchFailed)roundTripped.Observations[7];
        failed.DurationMs.Should().Be(100.0);
        failed.ErrorMessage.Should().Be("boom");
        failed.ExceptionType.Should().Be("InvalidOperationException");

        roundTripped.Observations[8].Should().BeOfType<HandlerFailed>();
        var handlerFailed = (HandlerFailed)roundTripped.Observations[8];
        handlerFailed.ErrorMessage.Should().Be("handler boom");
        handlerFailed.ExceptionType.Should().Be("NullReferenceException");

        roundTripped.Observations[9].Should().BeOfType<TransportSendFailed>();
        var sendFailed = (TransportSendFailed)roundTripped.Observations[9];
        sendFailed.ErrorMessage.Should().Be("send failed");
        sendFailed.ExceptionType.Should().Be("TimeoutException");

        roundTripped.Observations[10].Should().BeOfType<OutboxWritten>();
        ((OutboxWritten)roundTripped.Observations[10]).EntryCount.Should().Be(3);

        roundTripped.Observations[11].Should().BeOfType<OutboxSent>();
        ((OutboxSent)roundTripped.Observations[11]).EntryId.Should().Be(42);

        roundTripped.Observations[12].Should().BeOfType<InboxDuplicate>();

        roundTripped.Observations[13].Should().BeOfType<DeadLettered>();
        var deadLettered = (DeadLettered)roundTripped.Observations[13];
        deadLettered.Reason.Should().Be("max retries");
        deadLettered.SourceEndpoint.Should().Be("orders-queue");

        roundTripped.Observations[14].Should().BeOfType<CustomObservation>();
        var custom = (CustomObservation)roundTripped.Observations[14];
        custom.Name.Should().Be("my.metric");
    }
}
