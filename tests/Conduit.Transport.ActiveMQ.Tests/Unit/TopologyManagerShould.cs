using Amqp;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Integration;
using Conduit.Transport.ActiveMQ.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.ActiveMQ.Tests.Unit;

[TestClass]
public class TopologyManagerShould
{
    private static ConduitOptions CreateConduitOptions(
        string applicationName = "testapp",
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

    private static ActiveMQTopologyManager CreateManager(
        ActiveMQOptions? activeMqOptions = null,
        ConduitOptions? conduitOptions = null)
    {
        return new ActiveMQTopologyManager(
            activeMqOptions ?? new ActiveMQOptions { ConnectionString = "amqp://localhost:5672" },
            conduitOptions ?? CreateConduitOptions(),
            NullLogger.Instance);
    }

    // --- TM-1.2: UseSharedResourceNameForAnycastAddress ---

    [TestMethod]
    public void UseSharedResourceNameForAnycastAddress()
    {
        // Arrange — anycast address uses CreateSharedResourceName with "commands" suffix
        var conduitOptions = CreateConduitOptions();

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);

        // Assert
        anycastAddress.Should().Be("conduit-commands");
    }

    // --- TM-1.2b: UseSharedResourceNameForMulticastAddress ---

    [TestMethod]
    public void UseSharedResourceNameForMulticastAddress()
    {
        // Arrange — multicast address uses CreateSharedResourceName with "events" suffix
        var conduitOptions = CreateConduitOptions();

        // Act
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);

        // Assert
        multicastAddress.Should().Be("conduit-events");
    }

    // --- TM-1.5: UseSharedResourceNameForCommandQueues ---

    [TestMethod]
    public void UseSharedResourceNameForCommandQueues()
    {
        // Arrange — command/query queues are unicast (shared across apps, no app name)
        var conduitOptions = CreateConduitOptions();

        // Act
        var queueName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, "Orders.Commands.CreateOrder", 256);

        // Assert — system name + reversed segments, no app name
        queueName.Should().Be("conduit-CreateOrder-Commands-Orders");
    }

    [TestMethod]
    public void CommandQueueNamesAreIdenticalAcrossApps()
    {
        // Arrange — two different apps handling the same command type
        // should produce the same shared queue name (competing consumers)
        var optionsApp1 = CreateConduitOptions(applicationName: "app-one");
        var optionsApp2 = CreateConduitOptions(applicationName: "app-two");
        var commandType = "Orders.Commands.CreateOrder";

        // Act
        var queue1 = TopologyNaming.CreateSharedResourceName(optionsApp1, commandType, 256);
        var queue2 = TopologyNaming.CreateSharedResourceName(optionsApp2, commandType, 256);

        // Assert — both apps compute the same queue name
        queue1.Should().Be(queue2);
        queue1.Should().NotContain("app-one");
        queue1.Should().NotContain("app-two");
    }

    // --- TM-1.6: UseAppResourceNameForEventQueues ---

    [TestMethod]
    public void UseAppResourceNameForEventQueues()
    {
        // Arrange — event queues are multicast (per-app)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert — system name + app name
        queueName.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void EventQueueNamesAreDifferentAcrossApps()
    {
        // Arrange — two different apps subscribing to the same event type
        // should produce different per-app queue names (multicast)
        var optionsApp1 = CreateConduitOptions(applicationName: "app-one");
        var optionsApp2 = CreateConduitOptions(applicationName: "app-two");

        // Act
        var queue1 = TopologyNaming.CreateAppResourceName(optionsApp1, 256);
        var queue2 = TopologyNaming.CreateAppResourceName(optionsApp2, 256);

        // Assert — each app gets its own queue
        queue1.Should().NotBe(queue2);
        queue1.Should().Contain("app-one");
        queue2.Should().Contain("app-two");
    }

    // --- TM-1.7: TruncateLongNamesWithHash ---

    [TestMethod]
    public void TruncateLongNamesWithHash()
    {
        // Arrange — ActiveMQ Artemis max is 256
        var conduitOptions = CreateConduitOptions(applicationName: new string('x', 260));

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(256);
    }

    [TestMethod]
    public void TruncatedNamesContainHashSuffix()
    {
        // Arrange — app name long enough to trigger truncation at 256 chars
        // App resource name = "conduit-" + app name (260) = 268 chars, exceeds 256
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 260));

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert — truncated name contains 8-character hex hash suffix
        queueName.Length.Should().BeLessThanOrEqualTo(256);
        queueName.Should().MatchRegex(@"-[0-9a-f]{8}$");
    }

    // --- TM-1.8: IncludeTopologyPrefixInNames ---

    [TestMethod]
    public void IncludeDeveloperPrefixInNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "alice");

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert — developer prefix appears in all names
        anycastAddress.Should().Be("alice-conduit-commands");
        multicastAddress.Should().Be("alice-conduit-events");
        appQueueName.Should().Be("alice-conduit-orders");
    }

    [TestMethod]
    public void IncludeTestPrefixInNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Test,
            topologyPrefix: "test-abc123");

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert — test prefix appears in all names
        anycastAddress.Should().StartWith("test-abc123-conduit-commands");
        multicastAddress.Should().StartWith("test-abc123-conduit-events");
        appQueueName.Should().StartWith("test-abc123-conduit-orders");
    }

    // --- TM-4.1: ProductionModeDurableNoAutoDelete ---

    [TestMethod]
    public void ProductionModeDurableNoAutoDelete()
    {
        // Arrange
        var topologyMode = TopologyMode.Production;

        // Act
        var lifetime = ActiveMQTopologyManager.QueueLifetimeFor(topologyMode);

        // Assert — durable, and never auto-deleted
        lifetime.Durable.Should().BeTrue();
        lifetime.AutoDelete.Should().BeFalse();
    }

    [TestMethod]
    public void ProductionModeNoPrefixInNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);

        // Assert — no prefix in production mode
        anycastAddress.Should().Be("conduit-commands");
    }

    // --- TM-4.2: DeveloperModeAutoDeleteFourHours ---

    [TestMethod]
    public void DeveloperModeAutoDeleteFourHours()
    {
        // Arrange
        var topologyMode = TopologyMode.Developer;

        // Act
        var lifetime = ActiveMQTopologyManager.QueueLifetimeFor(topologyMode);

        // Assert — not durable; deleted 4 hours (14,400,000 ms) after its last consumer, however full
        lifetime.Durable.Should().BeFalse();
        lifetime.AutoDelete.Should().BeTrue();
        lifetime.AutoDeleteDelayMilliseconds.Should().Be(14_400_000L);
        lifetime.AutoDeleteMessageCount.Should().Be(-1);
    }

    // --- TM-4.3: TestModeAutoDeleteFiveMinutes ---

    [TestMethod]
    public void TestModeAutoDeleteFiveMinutes()
    {
        // Arrange
        var topologyMode = TopologyMode.Test;

        // Act
        var lifetime = ActiveMQTopologyManager.QueueLifetimeFor(topologyMode);

        // Assert — not durable; deleted 5 minutes (300,000 ms) after its last consumer, however full
        lifetime.Durable.Should().BeFalse();
        lifetime.AutoDelete.Should().BeTrue();
        lifetime.AutoDeleteDelayMilliseconds.Should().Be(300_000L);
        lifetime.AutoDeleteMessageCount.Should().Be(-1);
    }

    // --- TM-S.10: CreateSeparateAnycastAndMulticastAddresses ---

    [TestMethod]
    public void CreateSeparateAnycastAndMulticastAddresses()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);

        // Assert — two distinct addresses for different routing types
        anycastAddress.Should().NotBe(multicastAddress);
        anycastAddress.Should().Be("conduit-commands");
        multicastAddress.Should().Be("conduit-events");
    }

    // --- TM-S.11: UseAmqpFilterOnQueues ---

    [TestMethod]
    public void UseAmqpFilterOnQueues()
    {
        // Arrange — AMQP Subject maps to JMSType in Artemis core filter syntax
        var commandType = "Orders.Commands.CreateOrder";
        string[] eventTypes = ["Orders.Events.OrderPlaced", "Billing.Events.Invoiced"];

        // Act
        var commandFilter = ActiveMQTopologyManager.CommandQueueFilter(commandType);
        var eventFilter = ActiveMQTopologyManager.EventQueueFilter(eventTypes);

        // Assert — a command queue takes its own type; an event queue exactly the types it subscribes
        commandFilter.Should().Be("JMSType='Orders.Commands.CreateOrder'");
        eventFilter.Should().Be("JMSType IN ('Billing.Events.Invoiced','Orders.Events.OrderPlaced')");
    }

    [TestMethod]
    public void WriteTheSameEventFilterForAnyOrderOfTheSameTypes()
    {
        // Arrange — one set of event types, listed in two orders and once with a repeat
        string[] registryOrder = ["Orders.Events.OrderPlaced", "Billing.Events.Invoiced", "Billing.Events.Paid"];
        string[] poolOrder = ["Billing.Events.Paid", "Orders.Events.OrderPlaced", "Billing.Events.Invoiced"];
        string[] repeated = [.. poolOrder, "Billing.Events.Paid"];

        // Act
        var fromRegistryOrder = ActiveMQTopologyManager.EventQueueFilter(registryOrder);
        var fromPoolOrder = ActiveMQTopologyManager.EventQueueFilter(poolOrder);
        var fromRepeated = ActiveMQTopologyManager.EventQueueFilter(repeated);

        // Assert — the read-back comparison sees one filter, so a start does not rewrite it
        fromPoolOrder.Should().Be(fromRegistryOrder);
        fromRepeated.Should().Be(fromRegistryOrder);
        fromRegistryOrder.Should().Be(
            "JMSType IN ('Billing.Events.Invoiced','Billing.Events.Paid','Orders.Events.OrderPlaced')");
    }

    // --- TM-S.13: UseUniqueLinkNames ---

    [TestMethod]
    public void UseUniqueLinkNames()
    {
        // Arrange — consumers for two queue names that share their first 41 characters, and a short one:
        // a name cut from the queue's name to 50 characters made the first two equal and threw for the third
        string[] queueNames = [new string('q', 41) + "-first", new string('q', 41) + "-second", "c-a"];

        // Act
        var linkNames = queueNames.Select(_ => ActiveMQTopologyManager.ConsumerLinkName()).ToList();

        // Assert — every consumer link gets a name of its own, whatever its queue is called
        linkNames.Should().OnlyHaveUniqueItems();
        linkNames.Should().AllSatisfy(linkName => linkName.Should().MatchRegex("^consumer-[0-9a-f]{32}$"));
    }

    // --- TM-S.12: SetAutoDeleteDelayForEphemeralQueues ---

    [TestMethod]
    [DataRow(TopologyMode.Developer, 14_400_000L)]
    [DataRow(TopologyMode.Test, 300_000L)]
    public async Task SetAutoDeleteDelayForEphemeralQueues(TopologyMode topologyMode, long expectedDelayMilliseconds)
    {
        // Arrange — against the real broker: a delay only counts once Artemis holds it
        var connectionString = ActiveMQTestBase.RequireBroker();
        var conduitOptions = CreateConduitOptions(
            applicationName: "auto-delete",
            topologyMode: topologyMode,
            topologyPrefix: TestTopologyPrefix.NewForTest(typeof(TopologyManagerShould).Assembly));
        var manager = CreateManager(conduitOptions: conduitOptions);
        const string eventType = "Orders.Events.OrderPlaced";
        const string commandType = "Orders.Commands.PlaceOrder";

        // Act
        var connection = await new ConnectionFactory().CreateAsync(new Address(connectionString));
        try
        {
            await manager.EnsureTopologyAsync(new Session(connection), [eventType], [commandType], CancellationToken.None);
        }
        finally
        {
            await connection.CloseAsync();
        }

        // Assert — the event and the command queue alike: not durable, and deleted once they have had no
        // consumer for the mode's delay, whatever they still hold
        await using var management = await ArtemisManagement.ConnectAsync(connectionString);
        string[] queueNames =
        [
            TopologyNaming.CreateAppResourceName(conduitOptions, 256),
            TopologyNaming.CreateSharedResourceName(conduitOptions, commandType, 256),
        ];

        foreach (var queueName in queueNames)
        {
            var because = $"{queueName} is ephemeral";
            var configuration = await management.QueueConfigurationAsync(queueName);
            configuration.GetProperty("durable").GetBoolean().Should().BeFalse(because);
            configuration.GetProperty("auto-delete").GetBoolean().Should().BeTrue(because);
            configuration.GetProperty("auto-delete-delay").GetInt64().Should().Be(expectedDelayMilliseconds, because);
            configuration.GetProperty("auto-delete-message-count").GetInt64().Should().Be(-1, because);
        }
    }

    // --- Additional: Pool queue naming ---

    [TestMethod]
    public void UseAppResourceNameForPoolEventQueues()
    {
        // Arrange — pool queues for events use per-app naming
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "bulk", 256);

        // Assert — pool queue includes app name (per-app, multicast)
        poolQueueName.Should().Be("conduit-orders-bulk");
    }

    [TestMethod]
    public void UseSharedResourceNameForPoolCommandQueues()
    {
        // Arrange — pool queues for commands use shared naming with "pool.type" suffix
        var conduitOptions = CreateConduitOptions();

        // Act
        var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, "bulk.Orders.Commands.ImportBulk", 256);

        // Assert — shared name with pool+type suffix, no app name (unicast)
        sharedPoolQueueName.Should().Be("conduit-ImportBulk-Commands-Orders-bulk");
    }

    [TestMethod]
    public void AcceptConduitOptionsInConstructor()
    {
        // Arrange
        var activeMqOptions = new ActiveMQOptions { ConnectionString = "amqp://localhost:5672" };
        var conduitOptions = CreateConduitOptions();

        // Act
        var manager = CreateManager(activeMqOptions, conduitOptions);

        // Assert — construction succeeds without throwing
        manager.Should().NotBeNull();
    }
}
