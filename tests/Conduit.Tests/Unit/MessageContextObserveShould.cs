using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;

namespace Conduit.Tests.Unit;

[TestClass]
[DoNotParallelize]
public sealed class MessageContextObserveShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public void NoOpWhenNoObserverRegistered()
    {
        // Arrange — a context with no observer attached.
        MessageContext.StartDispatch();

        // Act & Assert — should not throw
        MessageContext.Observe(new DispatchInvoked("Test.Type", "Command"));
    }

    [TestMethod]
    public void NoOpWhenNoCurrentContext()
    {
        // Arrange — attach an observer to a context, then clear the current context.
        var collector = new ObservationCollector();
        MessageContext.StartDispatch().AttachObserver(collector);
        MessageContext.RestoreContext(null);

        // Act
        MessageContext.Observe(new DispatchInvoked("Test.Type", "Command"));

        // Assert — no current context, so nothing is observed.
        collector.Collected.Should().BeEmpty();
    }

    [TestMethod]
    public void ForwardObservationToRegisteredObserver()
    {
        // Arrange
        var collector = new ObservationCollector();
        MessageContext.StartDispatch().AttachObserver(collector);
        var observation = new DispatchInvoked("Test.Type", "Command");

        // Act
        MessageContext.Observe(observation);

        // Assert
        collector.Collected.Should().HaveCount(1);
        collector.Collected[0].Observation.Should().Be(observation);
    }

    [TestMethod]
    public void PassCurrentContextToObserver()
    {
        // Arrange
        var collector = new ObservationCollector();
        var context = MessageContext.StartDispatch();
        context.AttachObserver(collector);

        // Act
        MessageContext.Observe(new DispatchInvoked("Test.Type", "Command"));

        // Assert
        collector.Collected[0].MessageId.Should().Be(context.MessageId);
    }

    [TestMethod]
    public void FreezePropertiesFromDispatchOptions()
    {
        // Arrange
        var options = new DispatchOptions().Tag("partner", "ACME");

        // Act
        var context = MessageContext.StartDispatch(options);

        // Assert
        context.Properties.Should().ContainKey("partner").WhoseValue.Should().Be("ACME");
    }

    [TestMethod]
    public void HaveEmptyPropertiesWhenNoOptions()
    {
        // Arrange & Act
        var context = MessageContext.StartDispatch();

        // Assert
        context.Properties.Should().BeEmpty();
    }

    [TestMethod]
    public void NotLeakPropertiesAcrossDispatches()
    {
        // Arrange
        var options1 = new DispatchOptions().Tag("key", "value1");
        var context1 = MessageContext.StartDispatch(options1);
        MessageContext.RestoreContext(null);

        // Act
        var context2 = MessageContext.StartDispatch();

        // Assert
        context1.Properties.Should().ContainKey("key");
        context2.Properties.Should().BeEmpty();
    }

    [TestMethod]
    public void SetAndReadPayload()
    {
        // Arrange
        var context = MessageContext.StartDispatch();
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { id = 1 });

        // Act
        context.SetPayload(payload);

        // Assert
        context.Payload.Should().NotBeNull();
        context.Payload!.Value.GetProperty("id").GetInt32().Should().Be(1);
    }
}
