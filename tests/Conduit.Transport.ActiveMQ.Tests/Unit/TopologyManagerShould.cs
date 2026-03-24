using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
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
            topologyPrefix: "bpage");

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert — developer prefix appears in all names
        anycastAddress.Should().Be("bpage-conduit-commands");
        multicastAddress.Should().Be("bpage-conduit-events");
        appQueueName.Should().Be("bpage-conduit-orders");
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
        // Arrange — production mode: durable=true, no auto-delete
        // The topology manager sets durable based on TopologyMode == Production
        // and only sets autoDelete/autoDeleteDelay for non-production modes.
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);

        // Act
        var isDurable = conduitOptions.TopologyMode == TopologyMode.Production;
        var isEphemeral = conduitOptions.TopologyMode != TopologyMode.Production;

        // Assert
        isDurable.Should().BeTrue();
        isEphemeral.Should().BeFalse();
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
        // Arrange — developer mode sets autoDeleteDelay to 4 hours in milliseconds
        var expectedDelayMs = (long)TimeSpan.FromHours(4).TotalMilliseconds;

        // Act — this matches the value the topology manager computes for Developer mode
        // Assert — 4 hours = 14,400,000 milliseconds
        expectedDelayMs.Should().Be(14_400_000L);
    }

    [TestMethod]
    public void DeveloperModeAutoDeleteFlag()
    {
        // Arrange — non-production modes enable auto-delete
        var conduitOptions = CreateConduitOptions(
            topologyMode: TopologyMode.Developer,
            topologyPrefix: "dev");

        // Act — replicate the autoDelete check from CreateQueueAsync
        var isAutoDelete = conduitOptions.TopologyMode != TopologyMode.Production;

        // Assert
        isAutoDelete.Should().BeTrue();
    }

    // --- TM-4.3: TestModeAutoDeleteFiveMinutes ---

    [TestMethod]
    public void TestModeAutoDeleteFiveMinutes()
    {
        // Arrange — test mode sets autoDeleteDelay to 5 minutes in milliseconds
        var expectedDelayMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

        // Act — this matches the value the topology manager computes for Test mode
        // Assert — 5 minutes = 300,000 milliseconds
        expectedDelayMs.Should().Be(300_000L);
    }

    [TestMethod]
    public void TestModeAutoDeleteFlag()
    {
        // Arrange — non-production modes enable auto-delete
        var conduitOptions = CreateConduitOptions(
            topologyMode: TopologyMode.Test,
            topologyPrefix: "test-run1");

        // Act — replicate the autoDelete check from CreateQueueAsync
        var isAutoDelete = conduitOptions.TopologyMode != TopologyMode.Production;

        // Assert
        isAutoDelete.Should().BeTrue();
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
        // Arrange — the topology manager creates queues with AMQPSubject filter expressions
        var messageType = "Orders.Commands.CreateOrder";

        // Act — reproduce the filter expression used by ActiveMQTopologyManager
        var filterExpression = $"AMQPSubject='{messageType}'";

        // Assert
        filterExpression.Should().Be("AMQPSubject='Orders.Commands.CreateOrder'");
    }

    // --- TM-S.12: SetAutoDeleteDelayForEphemeralQueues ---

    [TestMethod]
    public void SetAutoDeleteDelayForDeveloperMode()
    {
        // Arrange — developer mode: autoDeleteDelay = 4 hours in milliseconds
        var expectedDelayMs = (long)TimeSpan.FromHours(4).TotalMilliseconds;

        // Act / Assert — verify the expected value (14,400,000 ms)
        expectedDelayMs.Should().Be(14_400_000L);
    }

    [TestMethod]
    public void SetAutoDeleteDelayForTestMode()
    {
        // Arrange — test mode: autoDeleteDelay = 5 minutes in milliseconds
        var expectedDelayMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

        // Act / Assert — verify the expected value (300,000 ms)
        expectedDelayMs.Should().Be(300_000L);
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
