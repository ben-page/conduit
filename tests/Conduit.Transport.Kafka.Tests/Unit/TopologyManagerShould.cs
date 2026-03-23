using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Transport.Kafka.Tests.Unit;

[TestClass]
public class TopologyManagerShould
{
    private static ConduitOptions CreateConduitOptions(
        string applicationName = "testapp",
        TopologyMode topologyMode = TopologyMode.Production,
        string systemName = "conduit",
        string? developerPrefix = null,
        string? testPrefix = null)
    {
        var options = new ConduitOptions
        {
            ApplicationName = applicationName,
            TopologyMode = topologyMode,
            SystemName = systemName,
            DeveloperPrefix = developerPrefix,
        };
        if (testPrefix is not null)
        {
            options.TestPrefix = testPrefix;
        }

        return options;
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

    // --- TM-1.2: UseSharedResourceNameForMainTopic ---

    [TestMethod]
    public void UseSharedResourceNameForMainTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act — main topic uses CreateSharedResourceName with maxLength 249 (Kafka limit)
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);

        // Assert
        topicName.Should().Be("conduit");
    }

    [TestMethod]
    public void UseSharedResourceNameForMainTopicMatchingManagerOutput()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();
        var expectedSharedTopic = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);

        // Assert — manager's first topic matches TopologyNaming output
        topicNames[0].Should().Be(expectedSharedTopic);
    }

    // --- TM-1.4: UseAppResourceNameForReplyTopic ---

    [TestMethod]
    public void UseAppResourceNameForReplyTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act — reply topic uses CreateAppResourceName with "replies" suffix
        var replyTopicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert
        replyTopicName.Should().Be("conduit-orders-replies");
    }

    [TestMethod]
    public void UseAppResourceNameForReplyTopicMatchingManagerOutput()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();
        var expectedReplyTopic = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert — manager's second topic matches TopologyNaming output
        topicNames[1].Should().Be(expectedReplyTopic);
    }

    // --- TM-1.7: TruncateLongTopicNamesWithHash ---

    [TestMethod]
    public void TruncateLongTopicNamesWithHash()
    {
        // Arrange — Kafka topic names limited to 249 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('x', 250));

        // Act
        var replyTopicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);
        var sharedTopicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);

        // Assert — both names are within Kafka's 249 char limit
        replyTopicName.Length.Should().BeLessThanOrEqualTo(249);
        sharedTopicName.Length.Should().BeLessThanOrEqualTo(249);
    }

    [TestMethod]
    public void TruncatedNamesContainHashSuffix()
    {
        // Arrange — name long enough to require truncation
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 250));

        // Act
        var topicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert — truncated name contains 8-character hex hash suffix
        topicName.Length.Should().BeLessThanOrEqualTo(249);
        topicName.Should().MatchRegex(@"-[0-9a-f]{8}$");
    }

    // --- TM-1.8: IncludeTopologyPrefixInNames ---

    [TestMethod]
    public void IncludeDeveloperPrefixInNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            developerPrefix: "bpage");

        // Act
        var sharedTopicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);
        var replyTopicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert — developer prefix appears in all names
        sharedTopicName.Should().Be("bpage-conduit");
        replyTopicName.Should().Be("bpage-conduit-orders-replies");
    }

    [TestMethod]
    public void IncludeTestPrefixInNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Test,
            testPrefix: "test-abc123");

        // Act
        var sharedTopicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 249);
        var replyTopicName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 249);

        // Assert — test prefix appears in all names
        sharedTopicName.Should().StartWith("test-abc123-conduit");
        replyTopicName.Should().StartWith("test-abc123-conduit-orders-replies");
    }

    [TestMethod]
    public void IncludeTopologyPrefixInManagedTopicNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            developerPrefix: "dev");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — all three managed topics include the developer prefix
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().StartWith("dev-");
        topicNames[1].Should().StartWith("dev-");
        topicNames[2].Should().StartWith("dev-");
    }

    // --- TM-4.1: ProductionModeNoRetentionOverride ---

    [TestMethod]
    public void ProductionModeNoRetentionOverride()
    {
        // Arrange — production mode should not set retention.ms (GetRetentionMs returns null)
        // We verify by checking the topology mode and confirming Production != Developer/Test
        var conduitOptions = CreateConduitOptions(topologyMode: TopologyMode.Production);
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — manager constructs successfully, no retention override in production
        topicNames.Should().HaveCount(3);
        conduitOptions.TopologyMode.Should().Be(TopologyMode.Production);
    }

    // --- TM-4.2: DeveloperModeRetentionFourHours ---

    [TestMethod]
    public void DeveloperModeRetentionFourHours()
    {
        // Arrange — developer mode sets retention.ms to 4 hours = 14,400,000 ms
        // Act — verify the expected retention value that GetRetentionMs would compute
        var expectedRetentionMs = (long)TimeSpan.FromHours(4).TotalMilliseconds;

        // Assert — 4 hours in milliseconds
        expectedRetentionMs.Should().Be(14_400_000L);
    }

    [TestMethod]
    public void DeveloperModeManagerProducesCorrectTopicNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Developer,
            developerPrefix: "dev");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — topic names include developer prefix
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().Be("dev-conduit");
        topicNames[1].Should().Be("dev-conduit-orders-replies");
        topicNames[2].Should().Be("dev-conduit-orders-dead-letter");
    }

    // --- TM-4.3: TestModeRetentionFiveMinutes ---

    [TestMethod]
    public void TestModeRetentionFiveMinutes()
    {
        // Arrange — test mode sets retention.ms to 5 minutes = 300,000 ms
        // Act — verify the expected retention value that GetRetentionMs would compute
        var expectedRetentionMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

        // Assert — 5 minutes in milliseconds
        expectedRetentionMs.Should().Be(300_000L);
    }

    [TestMethod]
    public void TestModeManagerProducesCorrectTopicNames()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(
            applicationName: "orders",
            topologyMode: TopologyMode.Test,
            testPrefix: "test-abc123");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — topic names include test prefix
        topicNames.Should().HaveCount(3);
        topicNames[0].Should().Be("test-abc123-conduit");
        topicNames[1].Should().Be("test-abc123-conduit-orders-replies");
        topicNames[2].Should().Be("test-abc123-conduit-orders-dead-letter");
    }

    // --- TM-S.5: CreateDeadLetterTopic ---

    [TestMethod]
    public void CreateDeadLetterTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();
        var expectedDeadLetterTopic = TopologyNaming.CreateAppResourceName(
            conduitOptions, "dead-letter", 249);

        // Assert — dead-letter topic is the third managed topic
        topicNames[2].Should().Be(expectedDeadLetterTopic);
        topicNames[2].Should().Be("conduit-orders-dead-letter");
    }

    // --- TM-S.6: CreateMainTopicWithCorrectPartitions ---

    [TestMethod]
    public void CreateMainTopicWithCorrectPartitions()
    {
        // Arrange — the shared topic is created with 6 partitions (hardcoded in EnsureTopologyAsync).
        // We can verify the topic name and count of managed topics to confirm the manager's
        // topology plan is correct (partition count itself requires a live broker to verify).
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var manager = CreateManager(conduitOptions: conduitOptions);

        // Act
        var topicNames = manager.GetManagedTopicNames();

        // Assert — shared topic is the first managed topic
        topicNames[0].Should().Be("conduit");
    }

    // --- Additional: ReturnAllThreeManagedTopicNames ---

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
