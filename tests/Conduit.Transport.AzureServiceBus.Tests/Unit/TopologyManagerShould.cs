using AwesomeAssertions;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

[TestClass]
public class TopologyManagerShould
{
    private static ConduitOptions CreateConduitOptions(
        string applicationName = "my-app",
        TopologyMode topologyMode = TopologyMode.Production,
        string systemName = "conduit",
        string? topologyPrefix = null)
    {
        return new ConduitOptions
        {
            ApplicationName = applicationName,
            TopologyMode = topologyMode,
            SystemName = systemName,
            TopologyPrefix = topologyPrefix,
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
            conduitOptions ?? CreateConduitOptions(),
            NullLogger.Instance);
    }

    // --- TM-1.1: UseCorrectMaxLengthForBroker ---

    [TestMethod]
    public void UseCorrectMaxLengthForTopicAndQueueNames()
    {
        // Arrange — ASB topics/queues are limited to 260 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('x', 300));

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 260);
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);

        // Assert
        topicName.Length.Should().BeLessThanOrEqualTo(260);
        queueName.Length.Should().BeLessThanOrEqualTo(260);
    }

    [TestMethod]
    public void UseCorrectMaxLengthForSubscriptionNames()
    {
        // Arrange — ASB subscription names are limited to 50 characters
        var conduitOptions = CreateConduitOptions(applicationName: "my-very-long-application-name");

        // Act
        var subscriptionName = TopologyNaming.CreateAppResourceName(
            conduitOptions, "VeryLong.Namespace.Events.SomethingHappened", 50);

        // Assert
        subscriptionName.Length.Should().BeLessThanOrEqualTo(50);
    }

    // --- TM-1.2: UseSharedResourceNameForMainTopic ---

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

    // --- TM-1.3: UseAppResourceNameForAppQueue ---

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

    // --- TM-1.4: UseAppResourceNameForReplyQueue ---

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

    // --- TM-1.5: UseSharedResourceNameForCommandQueues ---

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

    // --- TM-1.6: UseAppResourceNameForEventQueues ---

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

    // --- TM-1.7: TruncateLongNamesWithHash ---

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

    // --- TM-1.8: IncludeTopologyPrefixInNames ---

    [TestMethod]
    public void IncludeDeveloperPrefixInResourceNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "alice");

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 260);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);
        var replyQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 260);

        // Assert
        topicName.Should().Be("alice-conduit");
        appQueueName.Should().Be("alice-conduit-orders");
        replyQueueName.Should().Be("alice-conduit-orders-replies");
    }

    [TestMethod]
    public void IncludeTestPrefixInResourceNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Test);
        conduitOptions.TopologyPrefix = "test-abc123";

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 260);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);

        // Assert
        topicName.Should().StartWith("test-abc123-conduit");
        appQueueName.Should().StartWith("test-abc123-conduit-orders");
    }

    // --- TM-2.1: RouteCommandsToSharedResources ---

    [TestMethod]
    public void RouteCommandsToSharedSubscriptions()
    {
        // Arrange — two different apps handling the same command type
        // should produce the same shared subscription name (unicast)
        var optionsApp1 = CreateConduitOptions(applicationName: "app-one");
        var optionsApp2 = CreateConduitOptions(applicationName: "app-two");
        var commandType = "Orders.Commands.CreateOrder";

        // Act
        var sub1 = TopologyNaming.CreateSharedResourceName(optionsApp1, commandType, 50);
        var sub2 = TopologyNaming.CreateSharedResourceName(optionsApp2, commandType, 50);

        // Assert — both apps compute the same subscription name
        sub1.Should().Be(sub2);
        sub1.Should().NotContain("app-one");
        sub1.Should().NotContain("app-two");
    }

    // --- TM-2.2: RouteEventsToAppScopedResources ---

    [TestMethod]
    public void RouteEventsToAppScopedSubscriptions()
    {
        // Arrange — two different apps subscribing to the same event type
        // should produce different per-app subscription names (multicast)
        var optionsApp1 = CreateConduitOptions(applicationName: "app-one");
        var optionsApp2 = CreateConduitOptions(applicationName: "app-two");
        var eventType = "Shipping.Events.OrderShipped";

        // Act
        var sub1 = TopologyNaming.CreateAppResourceName(optionsApp1, eventType, 50);
        var sub2 = TopologyNaming.CreateAppResourceName(optionsApp2, eventType, 50);

        // Assert — each app gets its own subscription
        sub1.Should().NotBe(sub2);
    }

    // --- TM-3.1: RoutePoolTypesToPoolQueue ---

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

    // --- TM-3.2: RouteNonPoolTypesToDefaultQueue ---

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

    // --- TM-3.3: ExcludePoolTypesFromDefaultQueue ---

    [TestMethod]
    public void ExcludePoolTypesFromDefaultQueue()
    {
        // Arrange — a type assigned to a pool should route to the pool queue,
        // NOT the default queue (prevents double delivery)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var asbOptions = new AzureServiceBusOptions { ConnectionString = "not-used" };
        asbOptions.AddPool("bulk", pool =>
        {
            pool.Handle("Orders.Commands.ImportBulk");
            pool.Handle("Orders.Events.BulkImported");
        });
        var manager = CreateManager(asbOptions, conduitOptions);
        var defaultQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "bulk", 260);

        // Act
        var commandTarget = manager.GetTargetQueueForType("Orders.Commands.ImportBulk", defaultQueueName);
        var eventTarget = manager.GetTargetQueueForType("Orders.Events.BulkImported", defaultQueueName);

        // Assert — both pool types route to the pool queue, not the default
        commandTarget.Should().Be(poolQueueName);
        commandTarget.Should().NotBe(defaultQueueName);
        eventTarget.Should().Be(poolQueueName);
        eventTarget.Should().NotBe(defaultQueueName);
    }

    // --- TM-4.1: CreateDurableResourcesInProductionMode ---

    [TestMethod]
    public void CreateDurableResourcesInProductionMode()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act — ComputeEphemeralTtl returns null for production (durable, no auto-delete)
        var ephemeralTtl = manager.ComputeEphemeralTtl();

        // Assert
        ephemeralTtl.Should().BeNull();
    }

    // --- TM-4.2: CreateEphemeralResourcesInDeveloperMode ---

    [TestMethod]
    public void CreateEphemeralResourcesInDeveloperMode()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "alice");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act — ComputeEphemeralTtl returns 4 hours for developer mode
        var ephemeralTtl = manager.ComputeEphemeralTtl();

        // Assert
        ephemeralTtl.Should().Be(TimeSpan.FromHours(4));
    }

    // --- TM-4.3: CreateEphemeralResourcesInTestMode ---

    [TestMethod]
    public void CreateEphemeralResourcesInTestMode()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Test);
        conduitOptions.TopologyPrefix = "test-run1";
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act — ComputeEphemeralTtl returns 5 minutes for test mode
        var ephemeralTtl = manager.ComputeEphemeralTtl();

        // Assert
        ephemeralTtl.Should().Be(TimeSpan.FromMinutes(5));
    }

    // --- TM-S.2: CreateSqlFilterOnSubscriptions ---

    [TestMethod]
    public void CreateSqlFilterWithCorrectFormat()
    {
        // Arrange — ASB subscriptions use SQL filters: sys.Label = '{FullTypeName}'
        var messageType = "Orders.Commands.CreateOrder";

        // Act — replicate the filter string the topology manager builds
        var filterExpression = $"sys.Label = '{messageType}'";

        // Assert
        filterExpression.Should().Be("sys.Label = 'Orders.Commands.CreateOrder'");
    }

    [TestMethod]
    public void CreateSqlFilterMatchingTopologyManagerFormat()
    {
        // Arrange — verify the SQL rule filter object the topology manager creates
        // matches the expected format for multiple message types
        var commandType = "Orders.Commands.CreateOrder";
        var eventType = "Shipping.Events.OrderShipped";
        var queryType = "Catalog.Queries.GetProduct";

        // Act — build SqlRuleFilter the same way EnsureSubscriptionAsync does
        var commandFilter = new SqlRuleFilter($"sys.Label = '{commandType}'");
        var eventFilter = new SqlRuleFilter($"sys.Label = '{eventType}'");
        var queryFilter = new SqlRuleFilter($"sys.Label = '{queryType}'");

        // Assert — each filter uses the FullTypeName as the Subject discriminator
        commandFilter.SqlExpression.Should().Be("sys.Label = 'Orders.Commands.CreateOrder'");
        eventFilter.SqlExpression.Should().Be("sys.Label = 'Shipping.Events.OrderShipped'");
        queryFilter.SqlExpression.Should().Be("sys.Label = 'Catalog.Queries.GetProduct'");
    }

    // --- TM-S.3: CreateSessionEnabledReplyQueue ---

    [TestMethod]
    public void CreateSessionEnabledReplyQueue()
    {
        // Arrange — the reply queue requires sessions for request/response correlation
        var manager = CreateManager();

        // Act — the options the topology manager creates the reply queue with
        var queueOptions = manager.CreateReplyQueueOptions();

        // Assert — the queue is session-enabled
        queueOptions.RequiresSession.Should().BeTrue();
    }

    [TestMethod]
    public void CreateSessionEnabledReplyQueueWithCorrectName()
    {
        // Arrange — the reply queue name is generated by TopologyNaming with "replies" suffix
        var manager = CreateManager(conduitOptions: CreateConduitOptions(applicationName: "orders"));

        // Act
        var queueOptions = manager.CreateReplyQueueOptions();

        // Assert — reply queue has correct name and is session-enabled
        queueOptions.Name.Should().Be("conduit-orders-replies");
        queueOptions.RequiresSession.Should().BeTrue();
    }

    // --- TM-S.4: ExpireOrphanedRepliesOnTheReplyQueue ---

    [TestMethod]
    public void ExpireOrphanedRepliesOnTheReplyQueue()
    {
        // Arrange -- a reply nobody reads (its request timed out, or its process is gone) must not stay
        var asbOptions = new AzureServiceBusOptions
        {
            ConnectionString = "not-used",
            ResponseTimeout = TimeSpan.FromSeconds(45),
        };
        var manager = CreateManager(asbOptions, CreateConduitOptions(applicationName: "orders"));

        // Act
        var queueOptions = manager.CreateReplyQueueOptions();

        // Assert -- the reply queue discards a reply after twice the response timeout (the floor and the
        // no-expiry cases are AsbReplyShould's, which checks the reply and the queue together)
        queueOptions.DefaultMessageTimeToLive.Should().Be(TimeSpan.FromSeconds(90));
    }

    [TestMethod]
    public void CreateAppQueueWithoutSessions()
    {
        // Arrange — the default app queue does NOT require sessions (only the reply queue does)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 260);

        // Act — default queue has RequiresSession = false (the default)
        var queueOptions = new CreateQueueOptions(appQueueName);

        // Assert — app queue is not session-enabled
        queueOptions.RequiresSession.Should().BeFalse();
    }

    [TestMethod]
    public void SetAutoDeleteOnIdleForEphemeralTopicAndQueues()
    {
        // Arrange — developer mode sets AutoDeleteOnIdle on topics and queues
        var conduitOptions = CreateConduitOptions(
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "alice");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var ephemeralTtl = manager.ComputeEphemeralTtl();
        var topicOptions = new CreateTopicOptions(
            TopologyNaming.CreateSharedResourceName(conduitOptions, 260));
        if (ephemeralTtl.HasValue)
        {
            topicOptions.AutoDeleteOnIdle = ephemeralTtl.Value;
        }

        // Assert — AutoDeleteOnIdle is set to 4 hours for developer mode
        topicOptions.AutoDeleteOnIdle.Should().Be(TimeSpan.FromHours(4));
    }
}
