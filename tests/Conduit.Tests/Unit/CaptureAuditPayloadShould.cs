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
}
