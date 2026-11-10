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
/// Manages SNS+SQS topology: the topic; the app and pool queues, each with a dead-letter queue and a
/// redrive policy to it; the transport instance's own reply queue; and one subscription per queue, its
/// filter policy listing the queue's message types. Every step creates what is missing and brings what
/// already exists up to this release's settings, so it runs on every start.
/// </summary>
internal sealed class AmazonSqsTopologyManager
{
    /// <summary>The longest name SQS accepts for a queue.</summary>
    internal const int MaxQueueNameLength = 80;

    /// <summary>The most values SNS accepts in one filter policy. Each message type is one value.</summary>
    internal const int MaxFilterPolicyValues = 150;

    private const int MaxTopicNameLength = 256;

    // A queue's dead-letter queue is named after it. The queues that have one are named at most four
    // characters short of the limit, so that name fits too.
    private const string DeadLetterSuffix = "-dlq";
    private const int MaxDeadLetteredQueueNameLength = MaxQueueNameLength - 4;

    // A message moved to a dead-letter queue keeps its original enqueue time, and expires by it, so the
    // dead-letter queue keeps messages for the longest SQS allows (14 days), past the 4 days a queue keeps
    // them by default.
    private const string DeadLetterRetentionSeconds = "1209600";

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
    /// The URL of the instance's reply SQS queue. Set after <see cref="EnsureTopologyAsync"/> completes.
    /// </summary>
    internal string ReplyQueueAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Pool name to SQS queue URL mapping. Set after <see cref="EnsureTopologyAsync"/> completes.
    /// </summary>
    internal Dictionary<string, string> PoolQueueAddresses { get; } = new(StringComparer.Ordinal);

    /// <summary>The name of the SNS topic every message is published to, <c>{system}</c>.</summary>
    internal static string TopicName(ConduitOptions conduitOptions) =>
        TopologyNaming.CreateSharedResourceName(conduitOptions, MaxTopicNameLength);

    /// <summary>
    /// The name of the application's queue, <c>{system}-{app}</c>, kept short enough for its
    /// <see cref="DeadLetterQueueName"/> to fit.
    /// </summary>
    internal static string AppQueueName(ConduitOptions conduitOptions) =>
        TopologyNaming.CreateAppResourceName(conduitOptions, MaxDeadLetteredQueueNameLength);

    /// <summary>
    /// The name of a receiver pool's queue, <c>{system}-{app}-{pool}</c>, kept short enough for its
    /// <see cref="DeadLetterQueueName"/> to fit.
    /// </summary>
    internal static string PoolQueueName(ConduitOptions conduitOptions, string poolName) =>
        TopologyNaming.CreateAppResourceName(conduitOptions, poolName, MaxDeadLetteredQueueNameLength);

    /// <summary>
    /// The name of one transport instance's reply queue, <c>{system}-{app}-replies-{instanceId}</c>: each
    /// instance polls its own, so a reply reaches the instance that sent the request.
    /// </summary>
    internal static string ReplyQueueName(ConduitOptions conduitOptions, string instanceId) =>
        TopologyNaming.CreateAppResourceName(conduitOptions, $"replies-{instanceId}", MaxQueueNameLength);

    /// <summary>
    /// The name of the dead-letter queue of the queue named <paramref name="queueName"/>,
    /// <c>{queue}-dlq</c>.
    /// </summary>
    internal static string DeadLetterQueueName(string queueName) => queueName + DeadLetterSuffix;

    /// <summary>
    /// The filter policy of a queue's subscription, <c>{"Type": [...]}</c>: the queue's message types, each
    /// once, in ordinal order.
    /// </summary>
    /// <param name="messageTypes">The full type names the queue takes.</param>
    /// <param name="queueDescription">The queue, as the error names it.</param>
    /// <exception cref="InvalidOperationException">More than <see cref="MaxFilterPolicyValues"/> types:
    /// SNS would refuse the policy.</exception>
    internal static string FilterPolicy(IReadOnlyCollection<string> messageTypes, string queueDescription)
    {
        var values = messageTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (values.Length > MaxFilterPolicyValues)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "The SNS filter policy for {0} would list {1} message types, more than the {2} values SNS allows " +
                "in one filter policy. Move some of them to a receiver pool (AddPool): each pool's queue has a " +
                "subscription and a filter policy of its own.",
                queueDescription,
                values.Length,
                MaxFilterPolicyValues));
        }

        return JsonSerializer.Serialize(new Dictionary<string, string[]> { ["Type"] = values });
    }

    /// <summary>
    /// Creates only the outbound topology needed for publishing messages and
    /// receiving replies: the SNS topic and the instance's reply queue.
    /// Called when <see cref="ConduitOptions.ProducerOnly"/> is true.
    /// </summary>
    /// <param name="replyQueueName">The instance's reply queue (<see cref="ReplyQueueName"/>).</param>
    /// <param name="cancellationToken">Cancels the startup.</param>
    public async Task EnsureOutboundTopologyAsync(string replyQueueName, CancellationToken cancellationToken)
    {
        this.TopicArn = await this.CreateTopicAsync(cancellationToken);
        await this.CreateReplyQueueAsync(replyQueueName, cancellationToken);
    }

    /// <summary>
    /// Creates or updates all required topology: the SNS topic, the app and pool queues with their
    /// dead-letter queues, the instance's reply queue, and a filtered subscription per queue. Every filter
    /// policy is checked against SNS's limit before anything is created.
    /// </summary>
    /// <param name="replyQueueName">The instance's reply queue (<see cref="ReplyQueueName"/>).</param>
    /// <param name="subscribedEventTypes">The event types the application listens for.</param>
    /// <param name="handledCommandQueryTypes">The command and query types the application handles.</param>
    /// <param name="cancellationToken">Cancels the startup.</param>
    public async Task EnsureTopologyAsync(
        string replyQueueName,
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        // 1. Name the queues and plan each one's filter policy, refusing what SNS would refuse
        var applicationName = this.conduitOptions.ApplicationName;
        var appQueueName = AppQueueName(this.conduitOptions);
        var poolQueueNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var queueDescriptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [appQueueName] = $"the queue '{appQueueName}' of application '{applicationName}'",
        };
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = PoolQueueName(this.conduitOptions, pool.Name);
            poolQueueNames[pool.Name] = poolQueueName;
            queueDescriptions[poolQueueName] =
                $"the queue '{poolQueueName}' of receiver pool '{pool.Name}' of application '{applicationName}'";
        }

        RefuseAPoolQueueNamedLikeADeadLetterQueue(appQueueName, poolQueueNames);

        var filterPolicies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (queueName, messageTypes) in this.GroupByQueue(
            appQueueName, poolQueueNames, [.. subscribedEventTypes, .. handledCommandQueryTypes]))
        {
            filterPolicies[queueName] = FilterPolicy(messageTypes, queueDescriptions[queueName]);
        }

        // 2. The topic (CreateTopic returns the existing topic's ARN)
        this.TopicArn = await this.CreateTopicAsync(cancellationToken);

        // 3. The app and pool queues, each after the dead-letter queue its redrive policy names, then the
        // instance's reply queue
        var queueAddresses = new Dictionary<string, string>(StringComparer.Ordinal);
        this.AppQueueAddress = await this.CreateDeadLetteredQueueAsync(appQueueName, cancellationToken);
        queueAddresses[appQueueName] = this.AppQueueAddress;
        foreach (var (poolName, poolQueueName) in poolQueueNames)
        {
            var poolQueueAddress = await this.CreateDeadLetteredQueueAsync(poolQueueName, cancellationToken);
            this.PoolQueueAddresses[poolName] = poolQueueAddress;
            queueAddresses[poolQueueName] = poolQueueAddress;
        }

        await this.CreateReplyQueueAsync(replyQueueName, cancellationToken);

        // 4. Let the topic send to each queue, then subscribe each queue that takes a type: one
        // subscription per queue, since SNS keys subscriptions by (topic, protocol, endpoint)
        foreach (var (queueName, queueAddress) in queueAddresses)
        {
            var queueArn = await this.GetQueueArnAsync(queueAddress, cancellationToken);
            await this.SetSnsToSqsPolicyAsync(queueAddress, queueArn, cancellationToken);

            if (filterPolicies.TryGetValue(queueName, out var filterPolicy))
            {
                await this.SubscribeAsync(queueArn, filterPolicy, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Refuses a pool whose queue would be named like the dead-letter queue of another of the
    /// application's queues (a pool named <c>dlq</c> names the app queue's): that queue's dead letters
    /// would land on the pool's queue and be dispatched again.
    /// </summary>
    private static void RefuseAPoolQueueNamedLikeADeadLetterQueue(
        string appQueueName, Dictionary<string, string> poolQueueNames)
    {
        var deadLetterQueueNames = poolQueueNames.Values
            .Append(appQueueName)
            .Select(DeadLetterQueueName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (poolName, poolQueueName) in poolQueueNames)
        {
            if (deadLetterQueueNames.Contains(poolQueueName))
            {
                throw new InvalidOperationException(
                    $"The receiver pool '{poolName}' would name its queue '{poolQueueName}', the name of the " +
                    "dead-letter queue of another of the application's queues. Choose another pool name.");
            }
        }
    }

    /// <summary>
    /// The message types each queue takes, by queue name: a pooled type goes to the first pool that lists
    /// it, every other type to the app queue.
    /// </summary>
    private Dictionary<string, List<string>> GroupByQueue(
        string appQueueName, Dictionary<string, string> poolQueueNames, IReadOnlyList<string> messageTypes)
    {
        var typesByQueue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var messageType in messageTypes)
        {
            var pool = this.options.Pools.FirstOrDefault(
                candidate => candidate.MessageTypes.Contains(messageType, StringComparer.Ordinal));
            var queueName = pool is null ? appQueueName : poolQueueNames[pool.Name];

            if (!typesByQueue.TryGetValue(queueName, out var types))
            {
                types = [];
                typesByQueue[queueName] = types;
            }

            types.Add(messageType);
        }

        return typesByQueue;
    }

    private async Task<string> CreateTopicAsync(CancellationToken cancellationToken)
    {
        var response = await this.snsClient.CreateTopicAsync(
            new CreateTopicRequest { Name = TopicName(this.conduitOptions) }, cancellationToken);
        return response.TopicArn;
    }

    /// <summary>
    /// Creates or updates the dead-letter queue of the queue named <paramref name="queueName"/>, then the
    /// queue, with a redrive policy that moves a message there once SQS has delivered it
    /// <see cref="AmazonSqsOptions.MaxReceiveCount"/> times. Returns the queue's URL.
    /// </summary>
    private async Task<string> CreateDeadLetteredQueueAsync(string queueName, CancellationToken cancellationToken)
    {
        var deadLetterQueueAddress = await this.CreateOrUpdateQueueAsync(
            DeadLetterQueueName(queueName),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["MessageRetentionPeriod"] = DeadLetterRetentionSeconds,
            },
            cancellationToken);
        var deadLetterQueueArn = await this.GetQueueArnAsync(deadLetterQueueAddress, cancellationToken);

        var redrivePolicy = JsonSerializer.Serialize(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["deadLetterTargetArn"] = deadLetterQueueArn,
            ["maxReceiveCount"] = this.options.MaxReceiveCount,
        });

        return await this.CreateOrUpdateQueueAsync(
            queueName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["VisibilityTimeout"] = this.options.VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
                ["RedrivePolicy"] = redrivePolicy,
            },
            cancellationToken);
    }

    /// <summary>
    /// Creates or updates the instance's reply queue, with no dead-letter queue (a reply is deleted on
    /// receipt, whether anything awaits it). <see cref="ReplyQueueAddress"/> is set as soon as the queue
    /// exists, before its attributes are: the transport deletes the queue by it on stop or disposal, also
    /// after a start that failed at the next step.
    /// </summary>
    private async Task CreateReplyQueueAsync(string replyQueueName, CancellationToken cancellationToken)
    {
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["VisibilityTimeout"] = this.options.VisibilityTimeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };

        this.ReplyQueueAddress = await this.CreateOrFindQueueAsync(replyQueueName, attributes, cancellationToken);
        await this.SetAttributesAsync(this.ReplyQueueAddress, attributes, cancellationToken);
    }

    /// <summary>
    /// Creates the queue with <paramref name="attributes"/>, or finds it, then sets them. An existing queue
    /// otherwise keeps the attributes it was created with: SQS refuses to create a queue again with other
    /// values (<c>QueueNameExists</c>), so a changed visibility timeout or receive count could never reach
    /// a deployed system. Returns the queue's URL.
    /// </summary>
    private async Task<string> CreateOrUpdateQueueAsync(
        string queueName, Dictionary<string, string> attributes, CancellationToken cancellationToken)
    {
        var queueAddress = await this.CreateOrFindQueueAsync(queueName, attributes, cancellationToken);
        await this.SetAttributesAsync(queueAddress, attributes, cancellationToken);
        return queueAddress;
    }

    // Creates the queue with these attributes, or, when it exists with other values, looks it up
    private async Task<string> CreateOrFindQueueAsync(
        string queueName, Dictionary<string, string> attributes, CancellationToken cancellationToken)
    {
        try
        {
            var created = await this.sqsClient.CreateQueueAsync(
                new CreateQueueRequest { QueueName = queueName, Attributes = attributes },
                cancellationToken);
            return created.QueueUrl;
        }
        catch (QueueNameExistsException)
        {
            var existing = await this.sqsClient.GetQueueUrlAsync(
                new GetQueueUrlRequest { QueueName = queueName }, cancellationToken);
            return existing.QueueUrl;
        }
    }

    private async Task SetAttributesAsync(
        string queueAddress, Dictionary<string, string> attributes, CancellationToken cancellationToken)
    {
        await this.sqsClient.SetQueueAttributesAsync(
            new SetQueueAttributesRequest { QueueUrl = queueAddress, Attributes = attributes },
            cancellationToken);
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

    /// <summary>
    /// Subscribes the queue to the topic with raw message delivery (without it a filter policy cannot see
    /// the message attributes) and <paramref name="filterPolicy"/>, then sets both on the subscription,
    /// new or existing. SNS refuses to subscribe a queue again with other attributes ("Subscription
    /// already exists with different attributes"), which failed every start of a release that changed a
    /// queue's types; the existing subscription is then looked up by subscribing without attributes. A new
    /// subscription is created with both, so it never takes unfiltered messages in between.
    /// </summary>
    private async Task SubscribeAsync(string queueArn, string filterPolicy, CancellationToken cancellationToken)
    {
        string subscriptionArn;
        try
        {
            var subscribed = await this.snsClient.SubscribeAsync(
                new SubscribeRequest
                {
                    TopicArn = this.TopicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn,
                    ReturnSubscriptionArn = true,
                    Attributes = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["RawMessageDelivery"] = "true",
                        ["FilterPolicy"] = filterPolicy,
                    },
                },
                cancellationToken);
            subscriptionArn = subscribed.SubscriptionArn;
        }
        catch (InvalidParameterException exception)
            when (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            var existing = await this.snsClient.SubscribeAsync(
                new SubscribeRequest
                {
                    TopicArn = this.TopicArn,
                    Protocol = "sqs",
                    Endpoint = queueArn,
                    ReturnSubscriptionArn = true,
                },
                cancellationToken);
            subscriptionArn = existing.SubscriptionArn;
        }

        // Always, not only after a refusal: a broker that returns the existing subscription without applying
        // the attributes (LocalStack does) would otherwise keep the old filter policy
        await this.snsClient.SetSubscriptionAttributesAsync(
            new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subscriptionArn,
                AttributeName = "FilterPolicy",
                AttributeValue = filterPolicy,
            },
            cancellationToken);
        await this.snsClient.SetSubscriptionAttributesAsync(
            new SetSubscriptionAttributesRequest
            {
                SubscriptionArn = subscriptionArn,
                AttributeName = "RawMessageDelivery",
                AttributeValue = "true",
            },
            cancellationToken);
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
