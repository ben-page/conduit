using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;
using Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Tests the queues and subscriptions the transport creates and keeps up to date: a dead-letter queue and
/// redrive policy for every queue that takes messages, and attributes a later release changes applied to
/// what an earlier release created.
/// </summary>
[TestClass]
public class SqsTopologyShould : SqsTestBase
{
    [TestMethod]
    [Timeout(90_000)]
    public async Task CreateADeadLetterQueueWithARedrivePolicy()
    {
        // Arrange -- a listener that always fails, no Conduit dead-letter store, two receives allowed a
        // two-second visibility timeout apart, and a pool so that two queues take messages
        var listenerName = UniqueAppName("listener");
        await using var listenerHost = await CreateHostAsync(
            listenerName,
            configureOptions: options =>
            {
                options.MaxReceiveCount = 2;
                options.VisibilityTimeoutSeconds = 2;
                options.ResponseTimeout = TimeSpan.FromSeconds(2);
                options.AddPool("pooled", pool => pool.Handle<TestCommand>());
            });
        var prefix = listenerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(
            UniqueAppName("pub"), prefix, handling => handling.ClearAllHandlers());

        using var sqsClient = CreateSqsClient();
        var conduitOptions = listenerHost.GetRequiredService<ConduitOptions>();
        var appQueueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);
        var poolQueueName = AmazonSqsTopologyManager.PoolQueueName(conduitOptions, "pooled");
        var appDeadLetterQueueName = AmazonSqsTopologyManager.DeadLetterQueueName(appQueueName);
        var poolDeadLetterQueueName = AmazonSqsTopologyManager.DeadLetterQueueName(poolQueueName);

        // Act
        await publisherHost.GetRequiredService<IMediator>().PublishAsync(new ThrowingTestEvent("poison"));
        var deadLetter = await ReceiveOneAsync(sqsClient, appDeadLetterQueueName, TimeSpan.FromSeconds(45));

        // Assert -- each queue that takes messages redrives to its own dead-letter queue after two receives
        (await RedrivePolicyAsync(sqsClient, appQueueName)).Should()
            .Be((await QueueArnAsync(sqsClient, appDeadLetterQueueName), 2));
        (await RedrivePolicyAsync(sqsClient, poolQueueName)).Should()
            .Be((await QueueArnAsync(sqsClient, poolDeadLetterQueueName), 2));

        // ... while the dead-letter queues and the reply queue have no redrive policy of their own
        var replyQueuePrefix = TopologyNaming.CreateAppResourceName(conduitOptions, "replies", 80) + "-";
        var replyQueues = (await sqsClient.ListQueuesAsync(
            new ListQueuesRequest { QueueNamePrefix = replyQueuePrefix })).QueueUrls;
        replyQueues.Should().ContainSingle();
        (await AttributesAsync(sqsClient, replyQueues![0])).Should().NotContainKey("RedrivePolicy");
        (await AttributesAsync(sqsClient, await QueueUrlAsync(sqsClient, appDeadLetterQueueName)))
            .Should().NotContainKey("RedrivePolicy");
        (await AttributesAsync(sqsClient, await QueueUrlAsync(sqsClient, poolDeadLetterQueueName)))
            .Should().NotContainKey("RedrivePolicy");

        // ... and the failing event ran its listener twice, then moved to the dead-letter queue, intact
        deadLetter.Should().NotBeNull("the event should reach the dead-letter queue after two receives");
        deadLetter!.MessageAttributes["Type"].StringValue.Should().Be(typeof(ThrowingTestEvent).FullName);
        listenerHost.GetRequiredService<SpyLog>().Invocations.OfType<ThrowingTestEvent>().Should().HaveCount(2);
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task ApplyChangedAttributesToAnExistingQueueAndSubscription()
    {
        // Arrange -- a first release listened for TestEvent alone, at a two-second visibility timeout and the
        // default receive count, and has stopped; its queue and subscription stay. LocalStack keeps serving
        // its long poll, cancelled on stop, for up to 20 seconds, and may hand it the event published below:
        // the short visibility timeout returns the event from there in seconds.
        var listenerName = UniqueAppName("listener");
        string prefix;
        await using (var firstRelease = await CreateHostAsync(
            listenerName,
            configureHandling: handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>(),
            configureOptions: options =>
            {
                options.VisibilityTimeoutSeconds = 2;
                options.ResponseTimeout = TimeSpan.FromSeconds(2);
            }))
        {
            prefix = firstRelease.TopologyPrefix!;
        }

        // Act -- the next release, same name and prefix, changes both and also listens for FireAndForgetEvent
        await using var secondRelease = await CreateHostAsync(
            listenerName,
            prefix,
            handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>()
                .HandleEvent<FireAndForgetEvent, FireAndForgetEventListener>(),
            options =>
            {
                options.VisibilityTimeoutSeconds = 10;
                options.ResponseTimeout = TimeSpan.FromSeconds(10);
                options.MaxReceiveCount = 7;
            });
        await using var publisherHost = await CreateHostAsync(
            UniqueAppName("pub"), prefix, handling => handling.ClearAllHandlers());
        await publisherHost.GetRequiredService<IMediator>().PublishAsync(new FireAndForgetEvent("added"));

        // Assert -- the added type reached the release
        var listenerSpy = secondRelease.GetRequiredService<SpyLog>();
        await listenerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(45));
        listenerSpy.Invocations.Should().ContainSingle().Which.Should().Be(new FireAndForgetEvent("added"));

        // ... and the existing queue and subscription read back as the second release set them
        using var sqsClient = CreateSqsClient();
        using var snsClient = CreateSnsClient();
        var conduitOptions = secondRelease.GetRequiredService<ConduitOptions>();
        var appQueueName = AmazonSqsTopologyManager.AppQueueName(conduitOptions);

        var queueAttributes = await AttributesAsync(sqsClient, await QueueUrlAsync(sqsClient, appQueueName));
        queueAttributes["VisibilityTimeout"].Should().Be("10");
        (await RedrivePolicyAsync(sqsClient, appQueueName)).MaxReceiveCount.Should().Be(7);

        var subscriptionAttributes = await SubscriptionAttributesAsync(
            snsClient,
            AmazonSqsTopologyManager.TopicName(conduitOptions),
            await QueueArnAsync(sqsClient, appQueueName));
        subscriptionAttributes["RawMessageDelivery"].Should().Be("true");
        JsonSerializer.Deserialize<Dictionary<string, string[]>>(subscriptionAttributes["FilterPolicy"])!["Type"]
            .Should().BeEquivalentTo(typeof(TestEvent).FullName, typeof(FireAndForgetEvent).FullName);
    }

    private static async Task<Message?> ReceiveOneAsync(AmazonSQSClient sqsClient, string queueName, TimeSpan timeout)
    {
        var queueUrl = await QueueUrlAsync(sqsClient, queueName);
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = await sqsClient.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                WaitTimeSeconds = 5,
                MessageAttributeNames = ["All"],
            });

            if (response.Messages is [var message, ..])
            {
                return message;
            }
        }

        return null;
    }

    private static async Task<string> QueueUrlAsync(AmazonSQSClient sqsClient, string queueName) =>
        (await sqsClient.GetQueueUrlAsync(queueName)).QueueUrl;

    private static async Task<string> QueueArnAsync(AmazonSQSClient sqsClient, string queueName) =>
        (await AttributesAsync(sqsClient, await QueueUrlAsync(sqsClient, queueName)))["QueueArn"];

    private static async Task<Dictionary<string, string>> AttributesAsync(AmazonSQSClient sqsClient, string queueUrl) =>
        (await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = queueUrl,
            AttributeNames = ["All"],
        })).Attributes ?? [];

    // The redrive policy's target and receive count. SQS writes the count as a number, but accepts and
    // may echo it as a string.
    private static async Task<(string DeadLetterTargetArn, int MaxReceiveCount)> RedrivePolicyAsync(
        AmazonSQSClient sqsClient, string queueName)
    {
        var attributes = await AttributesAsync(sqsClient, await QueueUrlAsync(sqsClient, queueName));
        using var policy = JsonDocument.Parse(attributes["RedrivePolicy"]);
        var maxReceiveCount = policy.RootElement.GetProperty("maxReceiveCount");
        return (
            policy.RootElement.GetProperty("deadLetterTargetArn").GetString()!,
            maxReceiveCount.ValueKind == JsonValueKind.String
                ? int.Parse(maxReceiveCount.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                : maxReceiveCount.GetInt32());
    }

    private static async Task<Dictionary<string, string>> SubscriptionAttributesAsync(
        AmazonSimpleNotificationServiceClient snsClient, string topicName, string queueArn)
    {
        var topicArn = (await snsClient.CreateTopicAsync(new CreateTopicRequest { Name = topicName })).TopicArn;
        var subscriptions = (await snsClient.ListSubscriptionsByTopicAsync(
            new ListSubscriptionsByTopicRequest { TopicArn = topicArn })).Subscriptions ?? [];
        var subscription = subscriptions.Should()
            .ContainSingle(candidate => candidate.Endpoint == queueArn).Subject;

        return (await snsClient.GetSubscriptionAttributesAsync(
            new GetSubscriptionAttributesRequest { SubscriptionArn = subscription.SubscriptionArn })).Attributes;
    }
}
