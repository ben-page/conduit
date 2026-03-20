using AwesomeAssertions;
using Conduit.Runtime;

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
