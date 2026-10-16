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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(42.0), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchFailed(10.0, "boom", "InvalidOperationException"), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new HandlerStarted("TestHandler"), context);

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
            new DispatchInvoked("Test.Commands.PlaceOrder", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(5.0), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(3.0), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new HandlerStarted("DoThingHandler"), context);
        observer.OnObservation(
            new HandlerCompleted(20.0), context);
        observer.OnObservation(
            new DispatchCompleted(25.0), context);

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
            new DispatchInvoked("Orders.Commands.PlaceOrder", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(10.0), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(1.0), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(1.0), context);

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
            new DispatchInvoked("Test.Commands.DoThing", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Properties.Should().BeNull();
    }

    [TestMethod]
    public void SkipRecordingForSuppressedType()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var suppressedTypes = new HashSet<string> { "Test.Commands.Secret" };
        var observer = new AuditObserver(
            channel.Writer, "TestService", suppressedTypes, new HashSet<string>());
        var context = MessageContext.StartDispatch();

        // Act
        observer.OnObservation(
            new DispatchInvoked("Test.Commands.Secret", "Command"), context);
        observer.OnObservation(
            new HandlerStarted("SecretHandler"), context);
        observer.OnObservation(
            new DispatchCompleted(1.0), context);

        // Assert
        channel.Reader.TryRead(out _).Should().BeFalse(
            "a Suppress<T>()-marked type should produce no audit record");
    }

    [TestMethod]
    public void OmitPayloadForSuppressedPayloadType()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var suppressedPayloadTypes = new HashSet<string> { "Test.Commands.Sensitive" };
        var observer = new AuditObserver(
            channel.Writer, "TestService", new HashSet<string>(), suppressedPayloadTypes);
        var context = MessageContext.StartDispatch();
        context.SetPayload(JsonSerializer.SerializeToElement(new { ssn = "123-45-6789" }));

        // Act
        observer.OnObservation(
            new DispatchInvoked("Test.Commands.Sensitive", "Command"), context);
        observer.OnObservation(
            new DispatchCompleted(1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue(
            "a SuppressPayload<T>()-marked type should still produce a record");
        record!.MessageType.Should().Be("Test.Commands.Sensitive");
        record.Observations.Should().HaveCount(2);
        record.Payload.Should().BeNull("the sensitive payload must be omitted from the record");
    }

    [TestMethod]
    public void EvictStaleGroupOlderThanTimeout()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        var staleTimeout = TimeSpan.FromMinutes(5);

        // A dispatch that never emits a terminal observation (crash mid-handler).
        observer.OnObservation(
            new DispatchInvoked("Test.Commands.Crashes", "Command"), context);
        observer.OnObservation(
            new HandlerStarted("CrashingHandler"), context);

        // No terminal observation arrives. The group leaks unless swept.
        channel.Reader.TryRead(out _).Should().BeFalse("not yet swept");

        // Act: sweep with a 'now' well past the stale timeout.
        observer.EvictStaleGroups(DateTime.UtcNow.AddMinutes(10), staleTimeout);

        // Assert: the stale group is flushed (as incomplete) rather than leaked.
        channel.Reader.TryRead(out var record).Should().BeTrue(
            "a group with no terminal observation older than StaleGroupTimeout should be evicted");
        record!.MessageId.Should().Be(context.MessageId);
        record.MessageType.Should().Be("Test.Commands.Crashes");
        record.Observations.Should().Contain(observation => observation is DispatchFailed,
            "an evicted incomplete dispatch should be sealed as failed/incomplete");
    }

    [TestMethod]
    public void NotEvictFreshGroupWithinTimeout()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        var staleTimeout = TimeSpan.FromMinutes(5);

        observer.OnObservation(
            new DispatchInvoked("Test.Commands.InFlight", "Command"), context);

        // Act: sweep with a 'now' still within the timeout window.
        observer.EvictStaleGroups(DateTime.UtcNow.AddMinutes(1), staleTimeout);

        // Assert: an in-flight group younger than the timeout is left alone.
        channel.Reader.TryRead(out _).Should().BeFalse(
            "a group younger than StaleGroupTimeout must not be evicted");
    }

    [TestMethod]
    public void KeepAPayloadSetAfterDispatchInvoked()
    {
        // Arrange -- the wire path: DispatchInvoked is observed BEFORE the arm deserializes and captures
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked("Test.Commands.DoThing", "Request"), context);

        // Act
        context.SetPayload(JsonSerializer.SerializeToElement(new { value = "set-after-invoked" }));
        observer.OnObservation(new DispatchCompleted(1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Payload.Should().NotBeNull("the group re-reads the context's payload when it seals");
        record.Payload!.Value.GetRawText().Should().Contain("set-after-invoked");
    }

    [TestMethod]
    public void KeepAPayloadSetAfterDispatchInvokedOnAFailedDispatch()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked("Test.Commands.DoThing", "Request"), context);

        // Act
        context.SetPayload(JsonSerializer.SerializeToElement(new { value = "captured-then-failed" }));
        observer.OnObservation(new DispatchFailed(1.0, "boom", "InvalidOperationException"), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Payload!.Value.GetRawText().Should().Contain("captured-then-failed");
    }

    [TestMethod]
    public void KeepASuppressedPayloadOmittedWhenSetAfterInvoked()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(
            channel.Writer, "TestService", new HashSet<string>(), new HashSet<string> { "Test.Commands.Sensitive" });
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked("Test.Commands.Sensitive", "Request"), context);

        // Act -- the wire arm captures after DispatchInvoked; suppression must still win
        context.SetPayload(JsonSerializer.SerializeToElement(new { ssn = "123-45-6789" }));
        observer.OnObservation(new DispatchCompleted(1.0), context);

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Payload.Should().BeNull("SuppressPayload<T>() must not be undone by the seal-time re-read");
    }

    [TestMethod]
    public void KeepAPayloadSetAfterDispatchInvokedWhenTheGroupIsEvictedAsStale()
    {
        // Arrange -- a wire request that captured its payload, then ran past StaleGroupTimeout without finishing
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked("Test.Commands.Hangs", "Request"), context);
        context.SetPayload(JsonSerializer.SerializeToElement(new { value = "captured-then-evicted" }));

        // Act
        observer.EvictStaleGroups(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(5));

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Observations.Should().Contain(observation => observation is DispatchFailed);
        record.Payload.Should().NotBeNull("eviction seals through the same payload read as a terminal observation");
        record.Payload!.Value.GetRawText().Should().Contain("captured-then-evicted");
    }

    [TestMethod]
    public void KeepASuppressedPayloadOmittedWhenTheGroupIsEvictedAsStale()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(
            channel.Writer, "TestService", new HashSet<string>(), new HashSet<string> { "Test.Commands.Sensitive" });
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked("Test.Commands.Sensitive", "Request"), context);
        context.SetPayload(JsonSerializer.SerializeToElement(new { ssn = "123-45-6789" }));

        // Act
        observer.EvictStaleGroups(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(5));

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Payload.Should().BeNull();
    }

    [TestMethod]
    public void SealAFailedDispatchWithoutABodyClearedBeforeItClosed()
    {
        // Arrange -- a host refuses the request after capture and drops the body it must not keep
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService");
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked("Test.Queries.ListThings", "Request"), context);
        context.SetPayload(JsonSerializer.SerializeToElement(new { value = "must-not-be-kept" }));

        // Act
        context.ClearPayload();
        observer.OnObservation(new DispatchFailed(1.0, "refused", "BadRequestException"), context);

        // Assert -- the record still seals, failed, without a body
        channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.Payload.Should().BeNull();
        record.Observations.Should().HaveCount(2);
    }
}
