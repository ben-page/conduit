using AwesomeAssertions;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Runtime;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

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

    private static AzureServiceBusTopologyManager CreateManager(
        AzureServiceBusOptions? asbOptions = null,
        ConduitOptions? conduitOptions = null)
    {
        var adminClient = new ServiceBusAdministrationClient(
            "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=x");
        return new AzureServiceBusTopologyManager(
            adminClient,
            asbOptions ?? new AzureServiceBusOptions { ConnectionString = "not-used" },
            conduitOptions ?? CreateConduitOptions());
    }

    [TestMethod]
    public void UseTopologyNamingForTopicName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 260);

        // Assert
        topicName.Should().Be("conduit");
    }

    [TestMethod]
    public void UseTopologyNamingForAppQueueName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);

        // Assert
        appQueueName.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void UseTopologyNamingForReplyQueueName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var replyQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 260);

        // Assert
        replyQueueName.Should().Be("conduit-orders-replies");
    }

    [TestMethod]
    public void UseTopologyNamingForPoolQueueName()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "high-priority", 260);

        // Assert
        poolQueueName.Should().Be("conduit-orders-high-priority");
    }

    [TestMethod]
    public void UseSharedResourceNameForCommandSubscriptions()
    {
        // Arrange — command/query subscriptions are unicast (no app name)
        var conduitOptions = CreateConduitOptions();

        // Act
        var subscriptionName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, "Orders.Commands.CreateOrder", 50);

        // Assert — system name + reversed segments, no app name
        subscriptionName.Should().Be("conduit-CreateOrder-Commands-Orders");
    }

    [TestMethod]
    public void UseAppResourceNameForEventSubscriptions()
    {
        // Arrange — event subscriptions are multicast (include app name)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var subscriptionName = TopologyNaming.CreateAppResourceName(
            conduitOptions, "Shipping.Events.OrderShipped", 50);

        // Assert — system name + app name + reversed segments
        subscriptionName.Should().Be("conduit-orders-OrderShipped-Events-Shipping");
    }

    [TestMethod]
    public void RoutePoolMessageTypesToPoolQueue()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var asbOptions = new AzureServiceBusOptions { ConnectionString = "not-used" };
        asbOptions.AddPool("bulk", pool => pool.Handle("Orders.Commands.ImportBulk"));
        var manager = CreateManager(asbOptions, conduitOptions);
        var defaultQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);

        // Act
        var targetQueue = manager.GetTargetQueueForType("Orders.Commands.ImportBulk", defaultQueueName);

        // Assert
        var expectedPoolQueue = TopologyNaming.CreateAppResourceName(conduitOptions, "bulk", 260);
        targetQueue.Should().Be(expectedPoolQueue);
    }

    [TestMethod]
    public void RouteUnmatchedTypesToDefaultQueue()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var asbOptions = new AzureServiceBusOptions { ConnectionString = "not-used" };
        asbOptions.AddPool("bulk", pool => pool.Handle("Orders.Commands.ImportBulk"));
        var manager = CreateManager(asbOptions, conduitOptions);
        var defaultQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);

        // Act
        var targetQueue = manager.GetTargetQueueForType("Orders.Events.OrderCreated", defaultQueueName);

        // Assert
        targetQueue.Should().Be(defaultQueueName);
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
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 260);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);
        var replyQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 260);

        // Assert
        topicName.Should().Be("bpage-conduit");
        appQueueName.Should().Be("bpage-conduit-orders");
        replyQueueName.Should().Be("bpage-conduit-orders-replies");
    }

    [TestMethod]
    public void TruncateLongSubscriptionNamesWithHash()
    {
        // Arrange — subscription names are limited to 50 characters
        var conduitOptions = CreateConduitOptions(applicationName: "my-very-long-application-name");

        // Act
        var subscriptionName = TopologyNaming.CreateAppResourceName(
            conduitOptions, "VeryLong.Namespace.Events.SomethingHappened", 50);

        // Assert
        subscriptionName.Length.Should().BeLessThanOrEqualTo(50);
    }
}
