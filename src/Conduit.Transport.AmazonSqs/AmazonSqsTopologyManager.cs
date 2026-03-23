using System.Globalization;
using System.Text.Json;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Conduit.Configuration;
using Conduit.Runtime;

namespace Conduit.Transport.AmazonSqs;

/// <summary>
/// Manages SNS+SQS topology: topic creation, queue creation, subscription creation
/// with filter policies. All operations are idempotent.
/// </summary>
internal sealed class AmazonSqsTopologyManager
{
    private readonly IAmazonSimpleNotificationService snsClient;
    private readonly IAmazonSQS sqsClient;
    private readonly AmazonSqsOptions options;
    private readonly ConduitOptions conduitOptions;

    internal AmazonSqsTopologyManager(
        IAmazonSimpleNotificationService snsClient,
        IAmazonSQS sqsClient,
        AmazonSqsOptions options,
        ConduitOptions conduitOptions)
    {
        this.snsClient = snsClient;
        this.sqsClient = sqsClient;
        this.options = options;
        this.conduitOptions = conduitOptions;
    }

    /// <summary>
    /// The ARN of the SNS topic. Set after <see cref="EnsureTopologyAsync"/> completes.
    /// </summary>
    internal string TopicArn { get; private set; } = string.Empty;

    /// <summary>
    /// The URL of the default app SQS queue. Set after <see cref="EnsureTopologyAsync"/> completes.
    /// </summary>
    internal string AppQueueAddress { get; private set; } = string.Empty;

    /// <summary>
    /// The URL of the reply SQS queue. Set after <see cref="EnsureTopologyAsync"/> completes.
    /// </summary>
    internal string ReplyQueueAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Pool name to SQS queue URL mapping. Set after <see cref="EnsureTopologyAsync"/> completes.
    /// </summary>
    internal Dictionary<string, string> PoolQueueAddresses { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates only the outbound topology needed for publishing messages and
    /// receiving replies: the SNS topic and the reply SQS queue.
    /// Called when <see cref="ConduitOptions.ProducerOnly"/> is true.
    /// </summary>
    public async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        // 1. Create SNS topic (idempotent)
        var topicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 256);
        var topicResponse = await this.snsClient.CreateTopicAsync(
            new CreateTopicRequest { Name = topicName }, cancellationToken);
        this.TopicArn = topicResponse.TopicArn;

        // 2. Create reply SQS queue
        var replyQueueName = TopologyNaming.CreateAppResourceName(
            this.conduitOptions, "replies", 80);
        this.ReplyQueueAddress = await this.CreateQueueAsync(replyQueueName, cancellationToken);
    }

    /// <summary>
    /// Creates all required topology: SNS topic, SQS queues, SNS subscriptions with
    /// filter policies, and pool queues.
    /// </summary>
    public async Task EnsureTopologyAsync(
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        // 1. Create SNS topic (idempotent -- returns existing topic ARN if already exists)
        var topicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 256);
        var topicResponse = await this.snsClient.CreateTopicAsync(
            new CreateTopicRequest { Name = topicName }, cancellationToken);
        this.TopicArn = topicResponse.TopicArn;

        // 2. Create default app SQS queue
        var appQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 80);
        this.AppQueueAddress = await this.CreateQueueAsync(appQueueName, cancellationToken);
        var appQueueArn = await this.GetQueueArnAsync(this.AppQueueAddress, cancellationToken);

        // 3. Create reply SQS queue
        var replyQueueName = TopologyNaming.CreateAppResourceName(
            this.conduitOptions, "replies", 80);
        this.ReplyQueueAddress = await this.CreateQueueAsync(replyQueueName, cancellationToken);

        // 4. Create pool queues
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 80);
            var poolQueueAddress = await this.CreateQueueAsync(poolQueueName, cancellationToken);
            this.PoolQueueAddresses[pool.Name] = poolQueueAddress;

            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        // 5. Create SNS subscriptions for events (multicast -- per-app names)
        foreach (var eventType in subscribedEventTypes)
        {
            var targetQueueAddress = this.GetTargetQueueAddressForType(eventType, poolAssignedTypes);
            var targetQueueArn = await this.GetQueueArnAsync(targetQueueAddress, cancellationToken);
            await this.CreateFilteredSubscriptionAsync(
                targetQueueArn, eventType, cancellationToken);
        }

        // 6. Create SNS subscriptions for commands/queries (unicast -- shared names)
        foreach (var commandQueryType in handledCommandQueryTypes)
        {
            var targetQueueAddress = this.GetTargetQueueAddressForType(commandQueryType, poolAssignedTypes);
            var targetQueueArn = await this.GetQueueArnAsync(targetQueueAddress, cancellationToken);
            await this.CreateFilteredSubscriptionAsync(
                targetQueueArn, commandQueryType, cancellationToken);
        }

        // 7. Set SQS policy allowing SNS to send messages to the app queue (and pool queues)
        await this.SetSnsToSqsPolicyAsync(this.AppQueueAddress, appQueueArn, cancellationToken);
        foreach (var poolEntry in this.PoolQueueAddresses)
        {
            var poolQueueArn = await this.GetQueueArnAsync(poolEntry.Value, cancellationToken);
            await this.SetSnsToSqsPolicyAsync(poolEntry.Value, poolQueueArn, cancellationToken);
        }
    }

    private string GetTargetQueueAddressForType(
        string messageType, HashSet<string> poolAssignedTypes)
    {
        if (poolAssignedTypes.Contains(messageType))
        {
            foreach (var pool in this.options.Pools)
            {
                if (pool.MessageTypes.Contains(messageType, StringComparer.Ordinal))
                {
                    return this.PoolQueueAddresses[pool.Name];
                }
            }
        }

        return this.AppQueueAddress;
    }

    private async Task<string> CreateQueueAsync(
        string queueName, CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, string>
        {
            ["VisibilityTimeout"] = this.options.VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };

        var response = await this.sqsClient.CreateQueueAsync(
            new CreateQueueRequest
            {
                QueueName = queueName,
                Attributes = attributes,
            },
            cancellationToken);

        return response.QueueUrl;
    }

    private async Task<string> GetQueueArnAsync(
        string queueAddress, CancellationToken cancellationToken)
    {
        var response = await this.sqsClient.GetQueueAttributesAsync(
            new GetQueueAttributesRequest
            {
                QueueUrl = queueAddress,
                AttributeNames = ["QueueArn"],
            },
            cancellationToken);

        return response.QueueARN;
    }

    private async Task CreateFilteredSubscriptionAsync(
        string queueArn, string messageType, CancellationToken cancellationToken)
    {
        // Subscribe the SQS queue to the SNS topic
        await this.snsClient.SubscribeAsync(
            new SubscribeRequest
            {
                TopicArn = this.TopicArn,
                Protocol = "sqs",
                Endpoint = queueArn,
                Attributes = new Dictionary<string, string>
                {
                    // Raw message delivery -- required for filter policies on message attributes
                    ["RawMessageDelivery"] = "true",
                    // Filter policy on the Type message attribute
                    ["FilterPolicy"] = JsonSerializer.Serialize(
                        new Dictionary<string, string[]>
                        {
                            ["Type"] = [messageType],
                        }),
                },
            },
            cancellationToken);

        // SNS subscriptions are idempotent -- re-subscribing with the same
        // topic+protocol+endpoint returns the existing subscription ARN.
    }

    private async Task SetSnsToSqsPolicyAsync(
        string queueAddress, string queueArn, CancellationToken cancellationToken)
    {
        // Allow the SNS topic to send messages to this SQS queue
        var policy = JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Sid = "AllowSNSPublish",
                    Effect = "Allow",
                    Principal = new { Service = "sns.amazonaws.com" },
                    Action = "sqs:SendMessage",
                    Resource = queueArn,
                    Condition = new
                    {
                        ArnEquals = new Dictionary<string, string>
                        {
                            ["aws:SourceArn"] = this.TopicArn,
                        },
                    },
                },
            },
        });

        await this.sqsClient.SetQueueAttributesAsync(
            new SetQueueAttributesRequest
            {
                QueueUrl = queueAddress,
                Attributes = new Dictionary<string, string>
                {
                    ["Policy"] = policy,
                },
            },
            cancellationToken);
    }
}
