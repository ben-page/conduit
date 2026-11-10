using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Tests.Support;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitTypeRegistryShould
{
    [TestMethod]
    public void StoreEventTypes()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventTypes: [typeof(string), typeof(int)],
            commandTypes: [],
            queryTypes: []);

        // Act & Assert
        registry.EventTypes.Should().HaveCount(2);
        registry.EventTypes.Should().Contain(typeof(string));
        registry.EventTypes.Should().Contain(typeof(int));
    }

    [TestMethod]
    public void StoreCommandTypes()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventTypes: [],
            commandTypes: [typeof(string)],
            queryTypes: []);

        // Act & Assert
        registry.CommandTypes.Should().HaveCount(1);
        registry.CommandTypes.Should().Contain(typeof(string));
    }

    [TestMethod]
    public void StoreQueryTypes()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventTypes: [],
            commandTypes: [],
            queryTypes: [typeof(int)]);

        // Act & Assert
        registry.QueryTypes.Should().HaveCount(1);
        registry.QueryTypes.Should().Contain(typeof(int));
    }

    [TestMethod]
    public void PreComputeEventTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventTypes: [typeof(string), typeof(int)],
            commandTypes: [],
            queryTypes: []);

        // Act & Assert
        registry.EventTypeNames.Should().Contain("System.String");
        registry.EventTypeNames.Should().Contain("System.Int32");
    }

    [TestMethod]
    public void ActivateARuntimeRegisteredEvent()
    {
        // Arrange -- three discovered events, one with a listener the generator found; the other two gain theirs at
        // runtime (a Jobs trigger, a HandleEvent override), from several threads, each more than once
        var registry = new ConduitTypeRegistry(
            eventTypes: [typeof(ItemCreatedEvent), typeof(LocalOnlyEvent), typeof(SimpleEvent)],
            commandTypes: [],
            queryTypes: [],
            listenedEventTypes: [typeof(ItemCreatedEvent)]);
        var activated = new[] { typeof(LocalOnlyEvent).FullName!, typeof(SimpleEvent).FullName! };

        // Act
        Parallel.For(0, 64, index => registry.ActivateEvent(activated[index % 2]));

        // Assert -- each activated event is subscribed once, beside the compile-time one; what the registry knows is unchanged
        registry.ActiveEventTypeNames.Should().HaveCount(3).And.BeEquivalentTo(
            [typeof(ItemCreatedEvent).FullName!, .. activated]);
        registry.EventTypeNames.Should().BeEquivalentTo(
            [typeof(ItemCreatedEvent).FullName!, .. activated]);
    }

    [TestMethod]
    public void PreComputeCommandTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventTypes: [],
            commandTypes: [typeof(string)],
            queryTypes: []);

        // Act & Assert
        registry.CommandTypeNames.Should().Contain("System.String");
    }

    [TestMethod]
    public void PreComputeQueryTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventTypes: [],
            commandTypes: [],
            queryTypes: [typeof(int)]);

        // Act & Assert
        registry.QueryTypeNames.Should().Contain("System.Int32");
    }
}
