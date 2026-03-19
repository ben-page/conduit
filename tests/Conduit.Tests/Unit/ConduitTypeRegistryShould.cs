using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitTypeRegistryShould
{
    [TestMethod]
    public void StoreEventListenerTypes()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [typeof(string), typeof(int)],
            commandHandlers: [],
            queryHandlers: []);

        // Act & Assert
        registry.EventListeners.Should().HaveCount(2);
        registry.EventListeners.Should().Contain(typeof(string));
        registry.EventListeners.Should().Contain(typeof(int));
    }

    [TestMethod]
    public void StoreCommandHandlerTypes()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [],
            commandHandlers: [typeof(string)],
            queryHandlers: []);

        // Act & Assert
        registry.CommandHandlers.Should().HaveCount(1);
        registry.CommandHandlers.Should().Contain(typeof(string));
    }

    [TestMethod]
    public void StoreQueryHandlerTypes()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [],
            commandHandlers: [],
            queryHandlers: [typeof(int)]);

        // Act & Assert
        registry.QueryHandlers.Should().HaveCount(1);
        registry.QueryHandlers.Should().Contain(typeof(int));
    }

    [TestMethod]
    public void ReturnEventListenerFullTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [typeof(string), typeof(int)],
            commandHandlers: [],
            queryHandlers: []);

        // Act
        var names = registry.GetEventListenerNames();

        // Assert
        names.Should().Contain("System.String");
        names.Should().Contain("System.Int32");
    }

    [TestMethod]
    public void ReturnCommandHandlerFullTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [],
            commandHandlers: [typeof(string)],
            queryHandlers: []);

        // Act
        var names = registry.GetCommandHandlerNames();

        // Assert
        names.Should().Contain("System.String");
    }

    [TestMethod]
    public void ReturnQueryHandlerFullTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [],
            commandHandlers: [],
            queryHandlers: [typeof(int)]);

        // Act
        var names = registry.GetQueryHandlerNames();

        // Assert
        names.Should().Contain("System.Int32");
    }

    [TestMethod]
    public void ReturnAllHandledTypeNames()
    {
        // Arrange
        var registry = new ConduitTypeRegistry(
            eventListeners: [typeof(string)],
            commandHandlers: [typeof(int)],
            queryHandlers: [typeof(double)]);

        // Act
        var names = registry.GetAllHandledTypeNames();

        // Assert
        names.Should().HaveCount(3);
        names.Should().Contain("System.String");
        names.Should().Contain("System.Int32");
        names.Should().Contain("System.Double");
    }
}
