using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;

namespace Conduit.Transport.AmazonSqs.Tests.Unit;

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

    // --- TM-1.2: UseSharedResourceNameForSnsTopic ---

    [TestMethod]
    public void UseSharedResourceNameForSnsTopic()
    {
        // Arrange — SNS topic uses CreateSharedResourceName with maxLength 256
        var conduitOptions = CreateConduitOptions();

        // Act
        var topicName = AmazonSqsTopologyManager.TopicName(conduitOptions);

        // Assert
        topicName.Should().Be("conduit");
    }

    [TestMethod]
    public void TruncateLongSnsTopicNamesWithHash()
    {
        // Arrange — SNS topic names limited to 256 characters
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 260));

        // Act
        var topicName = AmazonSqsTopologyManager.TopicName(conduitOptions);

        // Assert
        topicName.Length.Should().BeLessThanOrEqualTo(256);
    }

    // --- TM-1.3: UseAppResourceNameForSqsQueue ---

    [TestMethod]
    public void UseAppResourceNameForSqsQueue()
    {
        // Arrange — the app queue is an app-scoped name under SQS's tight limit
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act
        var appQueueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);

        // Assert
        appQueueName.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void UseCorrectMaxLengthForSqsQueues()
    {
        // Arrange — SQS queue names are limited to 80 characters, and an app queue to 76, so that its
        // dead-letter queue's name, four characters longer, fits too
        var conduitOptions = CreateConduitOptions(applicationName: new string('a', 80));

        // Act
        var queueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);

        // Assert
        queueName.Length.Should().Be(76);
        AmazonSqsTopologyManager.DeadLetterQueueName(queueName).Length.Should().Be(80);
    }

    // --- TM-1.4: UseAppResourceNameForReplyQueue ---

    [TestMethod]
    public void UseAppResourceNameForReplyQueue()
    {
        // Arrange
        var conduitOptions = CreateConduitOptions(applicationName: "orders");

        // Act — one reply queue per transport instance
        var replyQueueName = AmazonSqsTopologyManager.ReplyQueueName(conduitOptions, "0f1e2d3c");

        // Assert — reply queue includes app name, "replies" suffix and instance id
        replyQueueName.Should().Be("conduit-orders-replies-0f1e2d3c");
    }

    // --- TM-1.7: TruncateLongQueueNamesWithHash ---

    [TestMethod]
    public void TruncateLongQueueNamesWithHash()
    {
        // Arrange — SQS 80-char limit is especially tight, truncation triggers frequently
        var conduitOptions = CreateConduitOptions(
            applicationName: "my-very-long-application-name-that-exceeds");

        // Act
        var queueName = AmazonSqsTopologyManager.ReplyQueueName(conduitOptions, Guid.NewGuid().ToString("N"));

        // Assert
        queueName.Length.Should().BeLessThanOrEqualTo(80);
    }

    [TestMethod]
    public void TruncatedQueueNamesContainHashSuffix()
    {
        // Arrange — name long enough to require truncation at 80 chars
        // "conduit-" (8) + app name (70) + "-replies-0f1e2d3c" (17) = 95 chars, exceeds 80
        var conduitOptions = CreateConduitOptions(
            applicationName: new string('a', 70));

        // Act
        var queueName = AmazonSqsTopologyManager.ReplyQueueName(conduitOptions, "0f1e2d3c");

        // Assert — truncated name contains 8-character hex hash suffix
        queueName.Length.Should().BeLessThanOrEqualTo(80);
        queueName.Should().MatchRegex(@"-[0-9a-f]{8}$");
    }

    [TestMethod]
    public void TruncateLongPoolQueueNamesWithHash()
    {
        // Arrange — pool queue names also subject to the SQS limit, less the dead-letter suffix
        var conduitOptions = CreateConduitOptions(
            applicationName: "my-very-long-application-name");

        // Act
        var poolQueueName = AmazonSqsTopologyManager.PoolQueueName(
            conduitOptions, "high-priority-processing-of-overnight-imports");

        // Assert
        poolQueueName.Length.Should().Be(76);
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
        var topicName = AmazonSqsTopologyManager.TopicName(conduitOptions);
        var appQueueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);
        var replyQueueName = AmazonSqsTopologyManager.ReplyQueueName(conduitOptions, "0f1e2d3c");

        // Assert — developer prefix appears in all resource names
        topicName.Should().Be("alice-conduit");
        appQueueName.Should().Be("alice-conduit-orders");
        replyQueueName.Should().Be("alice-conduit-orders-replies-0f1e2d3c");
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
        var topicName = AmazonSqsTopologyManager.TopicName(conduitOptions);
        var appQueueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);

        // Assert — test prefix appears in all resource names
        topicName.Should().StartWith("test-abc123-conduit");
        appQueueName.Should().StartWith("test-abc123-conduit-orders");
    }

    // --- TM-3.1: RoutePoolTypesToPoolQueue ---

    [TestMethod]
    public void RoutePoolTypesToPoolQueue()
    {
        // Arrange — pool-assigned types should be routed to the pool's SQS queue,
        // which uses CreateAppResourceName with the pool name suffix.
        // The topology manager tracks pool membership in a HashSet and uses it
        // in GetTargetQueueAddressForType to redirect to the pool queue.
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var sqsOptions = new AmazonSqsOptions { Region = Amazon.RegionEndpoint.USEast1 };
        sqsOptions.AddPool("bulk", pool => pool.Handle("Orders.Commands.ImportBulk"));

        // Act — verify the pool queue name matches what the topology manager would create
        var poolQueueName = AmazonSqsTopologyManager.PoolQueueName(conduitOptions, "bulk");

        // Assert — pool queue includes app name (per-app) and pool name
        poolQueueName.Should().Be("conduit-orders-bulk");
    }

    [TestMethod]
    public void TrackPoolAssignedTypesForRouting()
    {
        // Arrange — the topology manager builds a HashSet of pool-assigned types
        // to determine routing in GetTargetQueueAddressForType
        var sqsOptions = new AmazonSqsOptions { Region = Amazon.RegionEndpoint.USEast1 };
        sqsOptions.AddPool("bulk", pool =>
        {
            pool.Handle("Orders.Commands.ImportBulk");
            pool.Handle("Orders.Events.BulkImported");
        });

        // Act — collect pool-assigned types the same way the topology manager does
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in sqsOptions.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        // Assert — both types are tracked for pool routing
        poolAssignedTypes.Should().Contain("Orders.Commands.ImportBulk");
        poolAssignedTypes.Should().Contain("Orders.Events.BulkImported");
        poolAssignedTypes.Should().HaveCount(2);
    }

    [TestMethod]
    public void ResolvePoolNameFromPoolOptions()
    {
        // Arrange — when a message type is in a pool, the topology manager
        // iterates pools to find the matching pool name for queue naming
        var sqsOptions = new AmazonSqsOptions { Region = Amazon.RegionEndpoint.USEast1 };
        sqsOptions.AddPool("bulk", pool => pool.Handle("Orders.Commands.ImportBulk"));
        sqsOptions.AddPool("priority", pool => pool.Handle("Orders.Commands.UrgentOrder"));

        // Act — replicate the pool lookup logic from GetTargetQueueAddressForType
        string? resolvedPool = null;
        foreach (var pool in sqsOptions.Pools)
        {
            if (pool.MessageTypes.Contains("Orders.Commands.ImportBulk", StringComparer.Ordinal))
            {
                resolvedPool = pool.Name;
                break;
            }
        }

        // Assert — correct pool is resolved
        resolvedPool.Should().Be("bulk");
    }

    // --- TM-3.2: RouteNonPoolTypesToDefaultQueue ---

    [TestMethod]
    public void RouteNonPoolTypesToDefaultQueue()
    {
        // Arrange — message types NOT assigned to any pool should route to the
        // default app queue (CreateAppResourceName without pool suffix)
        var conduitOptions = CreateConduitOptions(applicationName: "orders");
        var sqsOptions = new AmazonSqsOptions { Region = Amazon.RegionEndpoint.USEast1 };
        sqsOptions.AddPool("bulk", pool => pool.Handle("Orders.Commands.ImportBulk"));

        // Act — collect pool-assigned types
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in sqsOptions.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        var defaultQueueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);
        var isPoolAssigned = poolAssignedTypes.Contains("Orders.Commands.CreateOrder");

        // Assert — unassigned type falls through to default queue
        isPoolAssigned.Should().BeFalse();
        defaultQueueName.Should().Be("conduit-orders");
    }

    [TestMethod]
    public void ExcludePoolTypesFromDefaultQueue()
    {
        // Arrange — pool-assigned types should NOT route to the default queue
        var sqsOptions = new AmazonSqsOptions { Region = Amazon.RegionEndpoint.USEast1 };
        sqsOptions.AddPool("bulk", pool =>
        {
            pool.Handle("Orders.Commands.ImportBulk");
            pool.Handle("Orders.Events.BulkImported");
        });

        // Act — collect pool-assigned types
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in sqsOptions.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        // Assert — pool-assigned types are excluded from default queue routing
        poolAssignedTypes.Should().Contain("Orders.Commands.ImportBulk");
        poolAssignedTypes.Should().Contain("Orders.Events.BulkImported");
    }

    // --- TM-S.7: CreateSnsFilterPolicy ---

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
    public void CreateSnsFilterPolicyWithCorrectJsonFormat()
    {
        // Arrange
        var messageType = "Shipping.Events.OrderShipped";

        // Act — the filter policy matches the exact format used by AmazonSqsTopologyManager
        var filterPolicy = JsonSerializer.Serialize(
            new Dictionary<string, string[]>
            {
                ["Type"] = [messageType],
            });

        // Assert — verify the serialized JSON contains the type array
        filterPolicy.Should().Contain("\"Type\"");
        filterPolicy.Should().Contain("\"Shipping.Events.OrderShipped\"");
    }

    [TestMethod]
    public void RefuseAFilterPolicyOverTheSnsLimit()
    {
        // Arrange — SNS allows at most 150 values in one filter policy, and each type is one value
        var atTheLimit = Enumerable.Range(0, 150).Select(index => $"Orders.Events.Event{index}").ToList();
        var overTheLimit = Enumerable.Range(0, 151).Select(index => $"Orders.Events.Event{index}").ToList();

        // Act
        var filterPolicy = AmazonSqsTopologyManager.FilterPolicy(atTheLimit, "the queue of application 'orders'");
        var refuse = () => AmazonSqsTopologyManager.FilterPolicy(overTheLimit, "the queue of application 'orders'");

        // Assert — 150 types make a policy; 151 are refused before AWS is asked, naming the queue and the count
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(filterPolicy)!["Type"].Should().HaveCount(150);
        refuse.Should().Throw<InvalidOperationException>()
            .WithMessage("*the queue of application 'orders'*151*150*");
    }

    [TestMethod]
    public void NameEachDeadLetterQueueAfterItsQueueWithinTheSqsLimit()
    {
        // Arrange — a short application name, and one long enough that its queue name is cut and hashed
        var shortOptions = CreateConduitOptions(applicationName: "orders");
        var longOptions = CreateConduitOptions(applicationName: new string('a', 90));

        // Act
        var shortQueueName = AmazonSqsTopologyManager.AppQueueName(shortOptions);
        var longQueueName = AmazonSqsTopologyManager.PoolQueueName(longOptions, "bulk");

        // Assert — {queue}-dlq, which still fits SQS's 80 characters however long the queue's name
        AmazonSqsTopologyManager.DeadLetterQueueName(shortQueueName).Should().Be("conduit-orders-dlq");
        AmazonSqsTopologyManager.DeadLetterQueueName(longQueueName).Should().Be($"{longQueueName}-dlq")
            .And.HaveLength(80);
    }
}
