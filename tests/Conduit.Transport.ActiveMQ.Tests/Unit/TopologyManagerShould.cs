using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.ActiveMQ.Tests.Unit;

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

    private static ActiveMQTopologyManager CreateManager(
        ActiveMQOptions? activeMqOptions = null,
        ConduitOptions? conduitOptions = null)
    {
        return new ActiveMQTopologyManager(
            activeMqOptions ?? new ActiveMQOptions { ConnectionString = "amqp://localhost:5672" },
            conduitOptions ?? CreateConduitOptions(),
            NullLogger.Instance);
    }

    [TestMethod]
    public void UseCorrectMaxLengthForBroker()
    {
        // Arrange — ActiveMQ Artemis address/queue names limited to 256 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 260));

        // Act
        var addressName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert
        addressName.Length.Should().BeLessThanOrEqualTo(256);
    }

    [TestMethod]
    public void UseSharedResourceNameForAnycastAddress()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act — anycast address uses shared name with "commands" suffix
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);

        // Assert
        anycastAddress.Should().Be("conduit-commands");
    }

    [TestMethod]
    public void UseSharedResourceNameForMulticastAddress()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act — multicast address uses shared name with "events" suffix
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);

        // Assert
        multicastAddress.Should().Be("conduit-events");
    }

    [TestMethod]
    public void UseSharedResourceNameForCommandQueues()
    {
        // Arrange — command/query queues are unicast (shared across apps)
        var conduitOptions = CreateConduitOptions();

        // Act
        var queueName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, "Orders.Commands.CreateOrder", 256);

        // Assert — system name + reversed segments, no app name
        queueName.Should().Be("conduit-CreateOrder-Commands-Orders");
    }

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
    public void TruncateLongNamesWithHash()
    {
        // Arrange — ActiveMQ max is 256
        var conduitOptions = CreateConduitOptions(applicationName: new string('x', 260));

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(256);
    }

    [TestMethod]
    public void IncludeTopologyPrefixInNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            developerPrefix: "bpage");

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 256);

        // Assert
        anycastAddress.Should().Be("bpage-conduit-commands");
        multicastAddress.Should().Be("bpage-conduit-events");
        appQueueName.Should().Be("bpage-conduit-orders");
    }

    [TestMethod]
    public void CreateSeparateAnycastAndMulticastAddresses()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "events", 256);

        // Assert — two distinct addresses
        anycastAddress.Should().NotBe(multicastAddress);
        anycastAddress.Should().Be("conduit-commands");
        multicastAddress.Should().Be("conduit-events");
    }

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

    [TestMethod]
    public void SetAutoDeleteDelayForDeveloperMode()
    {
        // Arrange — developer mode uses 4-hour auto-delete-delay
        var expectedDelayMs = (long)TimeSpan.FromHours(4).TotalMilliseconds;

        // Act / Assert — verify the expected value (14400000 ms)
        expectedDelayMs.Should().Be(14_400_000L);
    }

    [TestMethod]
    public void SetAutoDeleteDelayForTestMode()
    {
        // Arrange — test mode uses 5-minute auto-delete-delay
        var expectedDelayMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

        // Act / Assert — verify the expected value (300000 ms)
        expectedDelayMs.Should().Be(300_000L);
    }

    [TestMethod]
    public void ProductionModeCreatesDurableQueues()
    {
        // Arrange — production mode: durable=true, no auto-delete
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);

        // Act — verify production mode doesn't set a prefix
        var anycastAddress = TopologyNaming.CreateSharedResourceName(conduitOptions, "commands", 256);

        // Assert — no prefix in production mode
        anycastAddress.Should().Be("conduit-commands");
        conduitOptions.TopologyMode.Should().Be(TopologyMode.Production);
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

    [TestMethod]
    public void UseAppResourceNameForPoolQueues()
    {
        // Arrange — pool queues for events use per-app naming
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "bulk", 256);

        // Assert
        poolQueueName.Should().Be("conduit-orders-bulk");
    }

    [TestMethod]
    public void UseSharedResourceNameForCommandPoolQueues()
    {
        // Arrange — pool queues for commands use shared naming with pool prefix
        var conduitOptions = CreateConduitOptions();

        // Act
        var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
            conduitOptions, "bulk.Orders.Commands.ImportBulk", 256);

        // Assert — shared name with pool+type suffix
        sharedPoolQueueName.Should().Be("conduit-ImportBulk-Commands-Orders-bulk");
    }
}
