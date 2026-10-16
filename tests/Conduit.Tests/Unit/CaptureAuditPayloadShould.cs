using System.Text.Json;
using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;

namespace Conduit.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class CaptureAuditPayloadShould
{
    private sealed record MemberSecretRequest(string Name, [DoNotAudit] string Pin);

    private sealed record RawJsonRequest(string Name, JsonElement Body);

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public void ReturnTheSameMessageAndCaptureNothingWithoutAContext()
    {
        // Arrange
        MessageContext.RestoreContext(null);
        var message = new EchoCommand("no-context");

        // Act
        var returned = ConduitRuntime.CaptureAuditPayload(message);

        // Assert
        returned.Should().BeSameAs(message);
    }

    [TestMethod]
    public void SerializeNothingWhenNoObserverIsAttached()
    {
        // Arrange
        var context = MessageContext.StartDispatch();
        context.HasObserver.Should().BeFalse("nothing attached an observer to this context");

        // Act
        ConduitRuntime.CaptureAuditPayload(new EchoCommand("unobserved"));

        // Assert
        context.Payload.Should().BeNull("a host without an observer must not pay for serialization");
    }

    [TestMethod]
    public void CaptureTheRedactedPayloadWhenAnObserverIsAttached()
    {
        // Arrange
        var context = MessageContext.StartDispatch();
        context.AttachObserver(new ObservationCollector());
        context.HasObserver.Should().BeTrue();

        // Act
        ConduitRuntime.CaptureAuditPayload(new MemberSecretRequest("visible-name", "4321"));

        // Assert
        context.Payload.Should().NotBeNull();
        var raw = context.Payload!.Value.GetRawText();
        raw.Should().Contain("visible-name");
        raw.Should().NotContain("4321", "a [DoNotAudit] member is dropped by AuditJsonOptions");
        raw.Should().NotContainEquivalentOf("pin");
    }

    [TestMethod]
    public void StoreAJsonElementMemberAsTheRawJsonItHolds()
    {
        // Arrange -- AuditJsonOptions redacts members it can see as CLR properties; nothing under a JsonElement is one
        var context = MessageContext.StartDispatch();
        context.AttachObserver(new ObservationCollector());
        var body = JsonDocument.Parse("{\"pin\":\"4321\",\"anything\":\"the client invented\"}").RootElement;

        // Act
        ConduitRuntime.CaptureAuditPayload(new RawJsonRequest("visible-name", body));

        // Assert -- documented behaviour: a message carrying a raw-JSON member must be payload-suppressed
        var raw = context.Payload!.Value.GetProperty("body").GetRawText();
        raw.Should().Contain("4321").And.Contain("the client invented");
    }
}
