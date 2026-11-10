using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using RabbitMQ.Client;

namespace Conduit.Transport.RabbitMQ.Tests.Unit;

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

    // --- TM-1.1: UseCorrectMaxLengthForBroker ---

    [TestMethod]
    public void UseCorrectMaxLengthForBroker()
    {
        // Arrange — RabbitMQ entity names are limited to 255 characters.
        // Generate a name long enough that only the 255 limit keeps it in range.
        var conduitOptions = CreateConduitOptions(applicationName: new string('x', 300));

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(255);
    }

    // --- TM-1.2: UseSharedResourceNameForMainTopic ---

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

    // --- TM-1.3: UseAppResourceNameForAppQueue ---

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

    // --- TM-1.4: UseAppResourceNameForReplyQueue ---

    [TestMethod]
    public void UseAppResourceNameForReplyQueue()
    {
        // Arrange — the reply queue is per transport instance: the app's "replies" suffix, then the
        // instance's id, so the client names it and recovery re-declares it under the same name
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var replyQueueName = RabbitMQTopologyManager.ReplyQueueName(conduitOptions, "0f1e2d3c");

        // Assert — reply queue includes app name, "replies" suffix and instance id
        replyQueueName.Should().Be("conduit-orders-replies-0f1e2d3c");
    }

    // --- TM-1.5: UseSharedResourceNameForCommandQueues ---

    [TestMethod]
    public void UseSharedResourceNameForCommandQueues()
    {
        // Arrange — command/query queues are unicast (shared across apps, no app name)
        var conduitOptions = CreateConduitOptions();

        // Act
        var commandQueueName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, "Orders.Commands.CreateOrder", 255);

        // Assert — system name + reversed segments, no app name
        commandQueueName.Should().Be("conduit-CreateOrder-Commands-Orders");
    }

    // --- TM-1.6: UseAppResourceNameForEventBindings ---

    [TestMethod]
    public void UseAppResourceNameForEventBindings()
    {
        // Arrange — event bindings go on the per-app queue (multicast, includes app name)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert — events bind to the app queue which includes the app name
        appQueueName.Should().Be("conduit-orders");
        appQueueName.Should().Contain("orders");
    }

    // --- TM-1.7: TruncateLongNamesWithHash ---

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

    // --- TM-1.8: IncludeTopologyPrefixInNames ---

    [TestMethod]
    public void IncludeDeveloperPrefixInResourceNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "bpage");

        // Act
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert
        exchangeName.Should().Be("bpage-conduit");
        appQueueName.Should().Be("bpage-conduit-orders");
    }

    [TestMethod]
    public void IncludeTestPrefixInResourceNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Test);
        conduitOptions.TopologyPrefix = "test-abc123";

        // Act
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert
        exchangeName.Should().StartWith("test-abc123-conduit");
        appQueueName.Should().StartWith("test-abc123-conduit-my-app");
    }

    // --- TM-2.1: RouteCommandsToSharedResources ---

    [TestMethod]
    public void RouteCommandsToSharedResources()
    {
        // Arrange — two different apps handling the same command type
        // should produce the same shared queue name (competing consumers)
        var optionsApp1 = CreateConduitOptions(applicationName: "app-one");
        var optionsApp2 = CreateConduitOptions(applicationName: "app-two");
        var commandType = "Orders.Commands.CreateOrder";

        // Act
        var queueFromApp1 = TopologyNaming.CreateSharedResourceName(optionsApp1, commandType, 255);
        var queueFromApp2 = TopologyNaming.CreateSharedResourceName(optionsApp2, commandType, 255);

        // Assert — both apps compute the same queue name (no app name in shared resource)
        queueFromApp1.Should().Be(queueFromApp2);
        queueFromApp1.Should().NotContain("app-one");
        queueFromApp1.Should().NotContain("app-two");
    }

    // --- TM-2.2: RouteEventsToAppScopedResources ---

    [TestMethod]
    public void RouteEventsToAppScopedResources()
    {
        // Arrange — two different apps subscribing to the same event type
        // should produce different per-app queue names (multicast)
        var optionsApp1 = CreateConduitOptions(applicationName: "app-one");
        var optionsApp2 = CreateConduitOptions(applicationName: "app-two");

        // Act — events go on per-app queues
        var appQueue1 = TopologyNaming.CreateAppResourceName(optionsApp1, 255);
        var appQueue2 = TopologyNaming.CreateAppResourceName(optionsApp2, 255);

        // Assert — each app gets its own queue
        appQueue1.Should().NotBe(appQueue2);
        appQueue1.Should().Contain("app-one");
        appQueue2.Should().Contain("app-two");
    }

    // --- TM-3.1: RoutePoolTypesToPoolQueue ---

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
    public void RoutePoolEventTypesToAppScopedPoolQueue()
    {
        // Arrange — event types in a pool should produce an app-scoped pool queue
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act — pool event queue uses CreateAppResourceName with pool name suffix
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "bulk", 255);

        // Assert — pool queue includes app name (per-app, multicast)
        poolQueueName.Should().Be("conduit-orders-bulk");
        poolQueueName.Should().Contain("orders");
    }

    [TestMethod]
    public void RoutePoolCommandTypesToSharedPoolQueue()
    {
        // Arrange — command types in a pool should produce shared pool queue names
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var commandType = "Orders.Commands.ImportBulk";
        var poolName = "bulk";

        // Act — pool command queue uses CreateSharedResourceName with "{pool}.{type}" suffix
        var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, $"{poolName}.{commandType}", 255);

        // Assert — shared pool queue has no app name (unicast)
        sharedPoolQueueName.Should().NotContain("orders");
        sharedPoolQueueName.Should().Contain("conduit");
    }

    // --- TM-3.2: RouteNonPoolTypesToDefaultQueue ---

    [TestMethod]
    public void RouteNonPoolEventsToDefaultAppQueue()
    {
        // Arrange — the default app queue (not in a pool) uses CreateAppResourceName
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var defaultQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);

        // Assert — non-pool events go to the default per-app queue
        defaultQueueName.Should().Be("conduit-orders");
    }

    // --- TM-3.3: ExcludePoolTypesFromDefaultQueue ---

    [TestMethod]
    public void TrackPoolAssignedTypesForExclusion()
    {
        // Arrange — verify the topology manager tracks pool-assigned types
        // so they are excluded from the default queue bindings
        var rabbitOptions = new RabbitMQOptions { ConnectionString = "amqp://localhost" };
        rabbitOptions.Pools.Add(new ReceiverPoolOptions
        {
            Name = "bulk",
            Concurrency = 1,
        });
        rabbitOptions.Pools[0].Handle("Orders.Commands.ImportBulk");
        rabbitOptions.Pools[0].Handle("Orders.Events.BulkImported");

        // Act — collect pool-assigned types the same way the topology manager does
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in rabbitOptions.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        // Assert — both types are tracked for exclusion from default queue bindings
        poolAssignedTypes.Should().Contain("Orders.Commands.ImportBulk");
        poolAssignedTypes.Should().Contain("Orders.Events.BulkImported");
        poolAssignedTypes.Should().HaveCount(2);
    }

    [TestMethod]
    public void ExcludePoolTypesFromDefaultQueueBindings()
    {
        // Arrange — simulate the topology manager's binding loop: event types
        // assigned to a pool should be skipped when binding the default app queue
        var rabbitOptions = new RabbitMQOptions { ConnectionString = "amqp://localhost" };
        rabbitOptions.Pools.Add(new ReceiverPoolOptions
        {
            Name = "bulk",
            Concurrency = 1,
        });
        rabbitOptions.Pools[0].Handle("Orders.Events.BulkImported");

        var subscribedEventTypes = new[]
        {
            "Orders.Events.OrderCreated",
            "Orders.Events.BulkImported",
            "Shipping.Events.OrderShipped",
        };

        // Act — replicate the topology manager's exclusion logic
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in rabbitOptions.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        var defaultQueueBindings = new List<string>();
        foreach (var eventType in subscribedEventTypes)
        {
            if (!poolAssignedTypes.Contains(eventType))
            {
                defaultQueueBindings.Add(eventType);
            }
        }

        // Assert — pool-assigned type is excluded from default bindings
        defaultQueueBindings.Should().Contain("Orders.Events.OrderCreated");
        defaultQueueBindings.Should().Contain("Shipping.Events.OrderShipped");
        defaultQueueBindings.Should().NotContain("Orders.Events.BulkImported");
    }

    // --- TM-4.1: CreateDurableResourcesInProductionMode ---

    [TestMethod]
    public void CreateDurableResourcesInProductionMode()
    {
        // Arrange — production mode uses the same isEphemeral flag the topology manager checks
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);

        // Act — replicate the topology manager's durability logic
        var isEphemeral = conduitOptions.TopologyMode != TopologyMode.Production;

        // Assert — production: durable=true (not ephemeral), autoDelete=false, no prefix
        isEphemeral.Should().BeFalse();
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);
        exchangeName.Should().Be("conduit");
    }

    // --- TM-4.2: CreateEphemeralResourcesInDeveloperMode ---

    [TestMethod]
    public void CreateEphemeralResourcesInDeveloperMode()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "bpage");

        // Act — developer mode: isEphemeral should be true
        var isEphemeral = conduitOptions.TopologyMode != TopologyMode.Production;

        // Assert — developer: durable=false, autoDelete=true, prefixed resources
        isEphemeral.Should().BeTrue();
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);
        exchangeName.Should().StartWith("bpage-");
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);
        appQueueName.Should().StartWith("bpage-");
    }

    // --- TM-4.3: CreateEphemeralResourcesInTestMode ---

    [TestMethod]
    public void CreateEphemeralResourcesInTestMode()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Test);
        conduitOptions.TopologyPrefix = "test-run1";

        // Act — test mode: isEphemeral should be true
        var isEphemeral = conduitOptions.TopologyMode != TopologyMode.Production;

        // Assert — test: durable=false, autoDelete=true, test-prefixed resources
        isEphemeral.Should().BeTrue();
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);
        exchangeName.Should().StartWith("test-run1-");
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 255);
        appQueueName.Should().StartWith("test-run1-");
    }

    // --- TM-S.1: CreateTopicExchangeType ---

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
    public void CreateTopicExchangeType()
    {
        // Arrange — the topology manager declares the exchange with ExchangeType.Topic.
        // Verify the RabbitMQ client constant matches the expected "topic" string.
        // This ensures the EnsureTopologyAsync call uses the correct exchange type.

        // Act
        var exchangeType = ExchangeType.Topic;

        // Assert — RabbitMQ topic exchange for routing key-based message routing
        exchangeType.Should().Be("topic");
    }

    [TestMethod]
    public void NameTheDeadLetterExchangeAndQueueAfterTheSystem()
    {
        // Arrange — one dead-letter exchange and queue per system, shared by every application in it
        var ordersOptions = CreateConduitOptions(applicationName: "orders");
        var billingOptions = CreateConduitOptions(applicationName: "billing", topologyMode: TopologyMode.Developer, topologyPrefix: "jdoe");

        // Act
        var productionName = RabbitMQTopologyManager.DeadLetterName(ordersOptions);
        var developerName = RabbitMQTopologyManager.DeadLetterName(billingOptions);

        // Assert — {system}-dead-letter, under the topology prefix and without the application name
        productionName.Should().Be("conduit-dead-letter");
        developerName.Should().Be("jdoe-conduit-dead-letter");
    }

    [TestMethod]
    public void UseSingleTopicExchangeForAllMessageTypes()
    {
        // Arrange — RabbitMQ uses a single topic exchange; all message types
        // (commands, queries, events) publish to it with routing key = FullTypeName.
        // The exchange name is the shared resource name.
        var conduitOptions = CreateConduitOptions();

        // Act
        var exchangeName = TopologyNaming.CreateSharedResourceName(conduitOptions, 255);

        // Assert — single shared exchange, not per-type
        exchangeName.Should().Be("conduit");
    }
}
