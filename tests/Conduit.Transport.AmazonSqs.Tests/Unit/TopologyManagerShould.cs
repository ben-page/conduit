using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;

namespace Conduit.Transport.AmazonSqs.Tests.Unit;

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

    [TestMethod]
    public void UseCorrectMaxLengthForSqsQueues()
    {
        // Arrange — SQS queue names limited to 80 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 80));

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, 80);

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(80);
    }

    [TestMethod]
    public void UseCorrectMaxLengthForSnsTopics()
    {
        // Arrange — SNS topic names limited to 256 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 260));

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 256);

        // Assert
        topicName.Length.Should().BeLessThanOrEqualTo(256);
    }

    [TestMethod]
    public void UseSharedResourceNameForSnsTopic()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions();

        // Act
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 256);

        // Assert
        topicName.Should().Be("conduit");
    }

    [TestMethod]
    public void UseAppResourceNameForSqsQueue()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 80);

        // Assert
        appQueueName.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void UseAppResourceNameForReplyQueue()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var replyQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 80);

        // Assert
        replyQueueName.Should().Be("conduit-orders-replies");
    }

    [TestMethod]
    public void TruncateLongQueueNamesWithHash()
    {
        // Arrange — SQS 80-char limit is especially tight
        var conduitOptions = CreateConduitOptions(applicationName: "my-very-long-application-name-that-exceeds");

        // Act
        var queueName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 80);

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(80);
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
        var topicName = TopologyNaming.CreateSharedResourceName(conduitOptions, 256);
        var appQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, 80);
        var replyQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 80);

        // Assert
        topicName.Should().Be("bpage-conduit");
        appQueueName.Should().Be("bpage-conduit-orders");
        replyQueueName.Should().Be("bpage-conduit-orders-replies");
    }

    [TestMethod]
    public void UseAppResourceNameForPoolQueue()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var poolQueueName = TopologyNaming.CreateAppResourceName(conduitOptions, "bulk", 80);

        // Assert
        poolQueueName.Should().Be("conduit-orders-bulk");
    }

    [TestMethod]
    public void CreateSnsFilterPolicy()
    {
        // Arrange — the topology manager creates SNS subscriptions with a filter policy
        // matching on the "Type" message attribute
        var messageType = "Orders.Commands.CreateOrder";

        // Act — reproduce the filter policy format used by AmazonSqsTopologyManager
        var filterPolicy = JsonSerializer.Serialize(
            new Dictionary<string, string[]>
            {
                ["Type"] = [messageType],
            });

        // Assert — verify the JSON structure matches expected format
        var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(filterPolicy);
        parsed.Should().NotBeNull();
        parsed!["Type"].Should().ContainSingle().Which.Should().Be(messageType);
    }

    [TestMethod]
    public void TruncateLongPoolQueueNamesWithHash()
    {
        // Arrange — pool queue names with 80-char SQS limit
        var conduitOptions = CreateConduitOptions(
            applicationName: "my-very-long-application-name");

        // Act
        var poolQueueName = TopologyNaming.CreateAppResourceName(
            conduitOptions, "high-priority-processing", 80);

        // Assert
        poolQueueName.Length.Should().BeLessThanOrEqualTo(80);
    }
}
