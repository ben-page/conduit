using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Conduit.Audit.Runtime;
using Conduit.Runtime;

namespace Conduit.Audit.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class AuditObserverShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public void SealGroupOnDispatchCompleted()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 42.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.MessageId.Should().Be(context.MessageId);
        record.Observations.Should().HaveCount(2);
        record.Observations[0].Should().BeOfType<DispatchInvoked>();
        record.Observations[1].Should().BeOfType<DispatchCompleted>();
    }

    [TestMethod]
    public void SealGroupOnDispatchFailed()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchFailed(DateTimeOffset.UtcNow, 10.0, "boom", "InvalidOperationException"), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.MessageId.Should().Be(context.MessageId);
        record.Observations.Should().HaveCount(2);
        record.Observations[0].Should().BeOfType<DispatchInvoked>();
        record.Observations[1].Should().BeOfType<DispatchFailed>();
    }

    [TestMethod]
    public void NotWriteToChannelBeforeDispatchEnds()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new HandlerStarted(DateTimeOffset.UtcNow, "TestHandler"), context);

        // Assert
        channel.Reader.TryRead(out _).Should().BeFalse();
    }

    [TestMethod]
    public void CapturePayloadFromContext()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        context.SetPayload(JsonSerializer.SerializeToElement(new { orderId = 42 }));

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.PlaceOrder", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 5.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Payload.Should().NotBeNull();
        record.Payload!.Value.GetProperty("orderId").GetInt32().Should().Be(42);
    }

    [TestMethod]
    public void CapturePropertiesFromContext()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var options = new DispatchOptions().Tag("tenant", "acme").Tag("userId", "user-1");
        var context = MessageContext.StartDispatch(options);

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 3.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Properties.Should().NotBeNull();
        record.Properties!.Should().ContainKey("tenant").WhoseValue.Should().Be("acme");
        record.Properties!.Should().ContainKey("userId").WhoseValue.Should().Be("user-1");
    }

    [TestMethod]
    public void AccumulateMultipleObservations()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new HandlerStarted(DateTimeOffset.UtcNow, "DoThingHandler"), context);
        observer.OnObservation(
            new HandlerCompleted(DateTimeOffset.UtcNow, 20.0), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 25.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Observations.Should().HaveCount(4);
        record.Observations[0].Should().BeOfType<DispatchInvoked>();
        record.Observations[1].Should().BeOfType<HandlerStarted>();
        record.Observations[2].Should().BeOfType<HandlerCompleted>();
        record.Observations[3].Should().BeOfType<DispatchCompleted>();
    }

    [TestMethod]
    public void ExtractMessageTypeFromDispatchInvoked()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Orders.Commands.PlaceOrder", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 10.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.MessageType.Should().Be("Orders.Commands.PlaceOrder");
        record.MessageKind.Should().Be("Command");
    }

    [TestMethod]
    public void SetServiceNameOnRecord()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "OrderService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.ServiceName.Should().Be("OrderService");
    }

    [TestMethod]
    public void SetConversationAndCausationIds()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.ConversationId.Should().Be(context.ConversationId);
        record.CausationId.Should().Be(context.CausationId);
    }

    [TestMethod]
    public void HaveNullPropertiesWhenNoneProvided()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked(DateTimeOffset.UtcNow, "Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(DateTimeOffset.UtcNow, 1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Properties.Should().BeNull();
    }
}
