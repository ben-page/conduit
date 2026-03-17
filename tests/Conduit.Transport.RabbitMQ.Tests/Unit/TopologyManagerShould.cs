using AwesomeAssertions;

namespace Conduit.Transport.RabbitMQ.Tests.Unit;

[TestClass]
public class TopologyManagerShould
{
    [TestMethod]
    public void BuildAppQueueNameWithDefaultPrefix()
    {
        // Arrange
        var options = new RabbitMQOptions { ConnectionString = "amqp://localhost" };
        var manager = new RabbitMQTopologyManager(options, "my-app");

        // Act
        var queueName = manager.GetAppQueueName();

        // Assert
        queueName.Should().Be("conduit-my-app");
    }

    [TestMethod]
    public void BuildAppQueueNameWithCustomPrefix()
    {
        // Arrange
        var options = new RabbitMQOptions
        {
            ConnectionString = "amqp://localhost",
            QueuePrefix = "myprefix",
        };
        var manager = new RabbitMQTopologyManager(options, "my-app");

        // Act
        var queueName = manager.GetAppQueueName();

        // Assert
        queueName.Should().Be("myprefix-my-app");
    }

    [TestMethod]
    public void BuildCommandQueueNameCorrectly()
    {
        // Arrange
        var options = new RabbitMQOptions { ConnectionString = "amqp://localhost" };
        var manager = new RabbitMQTopologyManager(options, "my-app");

        // Act
        var queueName = manager.GetCommandQueueName("inventory-service");

        // Assert
        queueName.Should().Be("conduit-inventory-service");
    }
}
