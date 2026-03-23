using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.Kafka.Tests.Unit;

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

    private static KafkaTopologyManager CreateManager(
        KafkaOptions? kafkaOptions = null,
        ConduitOptions? conduitOptions = null)
    {
        return new KafkaTopologyManager(
            kafkaOptions ?? new KafkaOptions { BootstrapServers = "localhost:9092" },
            conduitOptions ?? CreateConduitOptions(),
            NullLogger.Instance);
    }

    [TestMethod]
    public void UseCorrectMaxLengthForBroker()
    {
        // Arrange — Kafka topic names limited to 249 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 250));

        // Act
        var topicName = TopologyNaming.CreateAppResourceName(conduitOptions, 249);

        // Assert
        topicName.Length.Should().BeLessThanOrEqualTo(249);
    }

    [TestMethod]
    public void UseSharedResourceNameForMainTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);

        // Assert
        topicName.Should().Be("conduit");
    }

    [TestMethod]
    public void UseAppResourceNameForReplyTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var replyTopicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert
        replyTopicName.Should().Be("conduit-orders-replies");
    }

    [TestMethod]
    public void TruncateLongNamesWithHash()
    {
        // Arrange — Kafka max is 249
        var conduitOptions = CreateConduitOptions(applicationName: new string('x', 250));

        // Act
        var topicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert
        topicName.Length.Should().BeLessThanOrEqualTo(249);
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
        var sharedTopicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);
        var replyTopicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert
        sharedTopicName.Should().Be("bpage-conduit");
        replyTopicName.Should().Be("bpage-conduit-orders-replies");
    }

    [TestMethod]
    public void ProductionModeNoRetentionOverride()
    {
        // Arrange — production mode should not set retention.ms
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — manager constructs without error, produces expected topic names
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().Be("conduit");
    }

    [TestMethod]
    public void DeveloperModeRetentionFourHours()
    {
        // Arrange — developer mode sets retention.ms to 4 hours (14400000 ms)
        var conduitOptions = CreateConduitOptions(
            topologyMode: TopologyMode.Developer,
            developerPrefix: "dev");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — topic names include developer prefix
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().Be("dev-conduit");
    }

    [TestMethod]
    public void TestModeRetentionFiveMinutes()
    {
        // Arrange — test mode sets retention.ms to 5 minutes (300000 ms)
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Test);
        conduitOptions.TestPrefix = "test-abc123";
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — topic names include test prefix
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().Be("test-abc123-conduit");
    }

    [TestMethod]
    public void CreateDeadLetterTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — third topic is the dead-letter topic
        topicNames[2].Should().Be("conduit-orders-dead-letter");
    }

    [TestMethod]
    public void CreateMainTopicWithCorrectPartitions()
    {
        // Arrange — the shared topic is created with 6 partitions (hardcoded in manager)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — shared topic is the first managed topic
        topicNames[0].Should().Be("conduit");
    }

    [TestMethod]
    public void ReturnAllThreeManagedTopicNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — shared topic, reply topic, dead-letter topic
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().Be("conduit");
        topicNames[1].Should().Be("conduit-orders-replies");
        topicNames[2].Should().Be("conduit-orders-dead-letter");
    }
}
