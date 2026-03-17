using AwesomeAssertions;
using Azure.Messaging.ServiceBus.Administration;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

[TestClass]
public class TopologyManagerShould
{
    private static AzureServiceBusTopologyManager CreateManager(
        AzureServiceBusOptions? options = null,
        string applicationName = "my-app")
    {
        var adminClient = new ServiceBusAdministrationClient(
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=x");
        return new AzureServiceBusTopologyManager(
            adminClient, options ?? new AzureServiceBusOptions { ConnectionString = "not-used" }, applicationName);
    }

    [TestMethod]
    public void BuildAppQueueNameWithDefaultPrefix()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var queueName = manager.GetQueueName("my-app");

        // Assert
        queueName.Should().Be("conduit-my-app");
    }

    [TestMethod]
    public void BuildAppQueueNameWithCustomPrefix()
    {
        // Arrange
        var options = new AzureServiceBusOptions
        {
            ConnectionString = "not-used",
            QueuePrefix = "myprefix",
        };
        var manager = CreateManager(options);

        // Act
        var queueName = manager.GetQueueName("my-app");

        // Assert
        queueName.Should().Be("myprefix-my-app");
    }

    [TestMethod]
    public void BuildReplyQueueNameCorrectly()
    {
        // Arrange
        var manager = CreateManager();

        // Act
        var replyQueueName = manager.GetReplyQueueName("my-app");

        // Assert
        replyQueueName.Should().Be("conduit-my-app-replies");
    }

    [TestMethod]
    public void BuildCommandQueueNameCorrectly()
    {
        // Act
        var commandQueueName = AzureServiceBusTopologyManager.GetCommandQueueName("inventory");

        // Assert
        commandQueueName.Should().Be("conduit-inventory");
    }

    [TestMethod]
    public void SanitizeSubscriptionNamesForAzureLimits()
    {
        // Arrange
        var shortName = "App.Events.OrderCreated";
        var longName = "MyApp.Domain.Events.VeryLongEventNameThatExceedsFiftyCharacterLimit";

        // Act
        var sanitizedShort = AzureServiceBusTopologyManager.SanitizeSubscriptionName(shortName);
        var sanitizedLong = AzureServiceBusTopologyManager.SanitizeSubscriptionName(longName);

        // Assert
        sanitizedShort.Should().Be("App-Events-OrderCreated");
        sanitizedShort.Should().NotContain(".");
        sanitizedLong.Length.Should().BeLessThanOrEqualTo(50);
        sanitizedLong.Should().NotContain(".");
    }
}
