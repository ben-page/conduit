using AwesomeAssertions;
using Conduit.Configuration;

namespace Conduit.Transport.RabbitMQ.Tests.Unit;

[TestClass]
public class TopologyManagerShould
{
    private static ConduitOptions CreateConduitOptions(
        string applicationName = "my-app",
        TopologyMode topologyMode = TopologyMode.Production,
        string systemName = "conduit",
        string? developerPrefix = null)
    {
        return new ConduitOptions
        {
            ApplicationName = applicationName,
            TopologyMode = topologyMode,
            SystemName = systemName,
            DeveloperPrefix = developerPrefix,
        };
    }

    [TestMethod]
    public void UseTopologyNamingForExchangeName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);

        // Assert
        exchangeName.Should().Be("conduit");
    }

    [TestMethod]
    public void UseTopologyNamingForAppQueueName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert
        appQueueName.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void UseTopologyNamingForPoolQueueName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "high-priority", 255);

        // Assert
        poolQueueName.Should().Be("conduit-orders-high-priority");
    }

    [TestMethod]
    public void IncludeDeveloperPrefixInResourceNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            developerPrefix: "bpage");

        // Act
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert
        exchangeName.Should().Be("bpage-conduit");
        appQueueName.Should().Be("bpage-conduit-orders");
    }

    [TestMethod]
    public void AcceptConduitOptionsInConstructor()
    {
        // Arrange
        var rabbitOptions = new RabbitMQOptions { ConnectionString = "amqp://localhost" };
        var conduitOptions = CreateConduitOptions();

        // Act
        var manager = new RabbitMQTopologyManager(rabbitOptions, conduitOptions);

        // Assert — construction succeeds without throwing
        manager.Should().NotBeNull();
    }

    [TestMethod]
    public void TruncateLongQueueNamesWithHash()
    {
        // Arrange — RabbitMQ queue names limited to 255 characters
        var conduitOptions = CreateConduitOptions(
            applicationName: new string('a', 250));

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(255);
    }
}
