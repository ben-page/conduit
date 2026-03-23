using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Runtime;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Manages Azure Service Bus topology: topics, queues, subscriptions.
/// Uses try-create with 409 conflict swallowing for idempotent startup across pods.
/// </summary>
internal sealed class AzureServiceBusTopologyManager
{
    private readonly ServiceBusAdministrationClient adminClient;
    private readonly AzureServiceBusOptions options;
    private readonly ConduitOptions conduitOptions;

    internal AzureServiceBusTopologyManager(
        ServiceBusAdministrationClient adminClient,
        AzureServiceBusOptions options,
        ConduitOptions conduitOptions)
    {
        this.adminClient = adminClient;
        this.options = options;
        this.conduitOptions = conduitOptions;
    }

    /// <summary>
    /// Creates only the outbound topology needed for publishing messages and
    /// receiving replies: the shared topic and the reply queue.
    /// Called when <see cref="ConduitOptions.ProducerOnly"/> is true.
    /// </summary>
    public async Task EnsureOutboundTopologyAsync(CancellationToken cancellationToken)
    {
        var ephemeralTtl = this.ComputeEphemeralTtl();

        // Shared event topic
        var topicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 260);
        await this.TryCreateTopicAsync(topicName, ephemeralTtl, cancellationToken);

        // Reply queue with sessions enabled for request/response correlation
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "replies", 260);
        await this.TryCreateQueueAsync(replyQueueName, requiresSession: true, ephemeralTtl, cancellationToken);
    }

    /// <summary>
    /// Creates all required topology for the transport: topic, app queue, reply queue,
    /// event subscriptions (multicast, per-app) and command/query subscriptions (unicast, shared),
    /// and pool queues.
    /// </summary>
    public async Task EnsureTopologyAsync(
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        var ephemeralTtl = this.ComputeEphemeralTtl();

        // Shared event topic
        var topicName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 260);
        await this.TryCreateTopicAsync(topicName, ephemeralTtl, cancellationToken);

        // Default app queue for receiving events forwarded from subscriptions
        var appQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 260);
        await this.TryCreateQueueAsync(appQueueName, requiresSession: false, ephemeralTtl, cancellationToken);

        // Reply queue with sessions enabled for request/response correlation
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "replies", 260);
        await this.TryCreateQueueAsync(replyQueueName, requiresSession: true, ephemeralTtl, cancellationToken);

        // Event subscriptions: multicast (per-app, includes app name)
        foreach (var eventType in subscribedEventTypes)
        {
            var targetQueue = this.GetTargetQueueForType(eventType, appQueueName);
            var subscriptionName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, eventType, 50);
            await this.TryCreateSubscriptionAsync(
                topicName, subscriptionName, eventType, targetQueue, ephemeralTtl, cancellationToken);
        }

        // Command/query subscriptions: unicast (shared, no app name)
        foreach (var commandQueryType in handledCommandQueryTypes)
        {
            var targetQueue = this.GetTargetQueueForType(commandQueryType, appQueueName);
            var subscriptionName = TopologyNaming.CreateSharedResourceName(
                this.conduitOptions, commandQueryType, 50);
            await this.TryCreateSubscriptionAsync(
                topicName, subscriptionName, commandQueryType, targetQueue, ephemeralTtl, cancellationToken);
        }

        // Pool queues with separate processors
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 260);
            await this.TryCreateQueueAsync(
                poolQueueName, requiresSession: false, ephemeralTtl, cancellationToken);
        }
    }

    /// <summary>
    /// Determines which queue a message type should be routed to, considering pool assignments.
    /// Returns the pool queue name if the type is assigned to a pool, otherwise the default queue.
    /// </summary>
    internal string GetTargetQueueForType(string messageType, string defaultQueueName)
    {
        foreach (var pool in this.options.Pools)
        {
            if (pool.MessageTypes.Contains(messageType, StringComparer.Ordinal))
            {
                return TopologyNaming.CreateAppResourceName(this.conduitOptions, pool.Name, 260);
            }
        }

        return defaultQueueName;
    }

    internal TimeSpan? ComputeEphemeralTtl()
    {
        return this.conduitOptions.TopologyMode switch
        {
            TopologyMode.Production => null,
            TopologyMode.Developer => TimeSpan.FromHours(4),
            TopologyMode.Test => TimeSpan.FromMinutes(5),
            _ => null,
        };
    }

    private async Task TryCreateTopicAsync(
        string name,
        TimeSpan? autoDeleteOnIdle,
        CancellationToken cancellationToken)
    {
        try
        {
            var topicOptions = new CreateTopicOptions(name);
            if (autoDeleteOnIdle.HasValue)
            {
                topicOptions.AutoDeleteOnIdle = autoDeleteOnIdle.Value;
            }

            await this.adminClient.CreateTopicAsync(topicOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Topic already exists — another pod created it first.
        }
    }

    private async Task TryCreateQueueAsync(
        string name,
        bool requiresSession,
        TimeSpan? autoDeleteOnIdle,
        CancellationToken cancellationToken)
    {
        try
        {
            var queueOptions = new CreateQueueOptions(name)
            {
                RequiresSession = requiresSession,
            };

            if (autoDeleteOnIdle.HasValue)
            {
                queueOptions.AutoDeleteOnIdle = autoDeleteOnIdle.Value;
            }

            await this.adminClient.CreateQueueAsync(queueOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Queue already exists — another pod created it first.
        }
    }

    private async Task TryCreateSubscriptionAsync(
        string topicName,
        string subscriptionName,
        string messageType,
        string forwardTo,
        TimeSpan? autoDeleteOnIdle,
        CancellationToken cancellationToken)
    {
        try
        {
            var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                ForwardTo = forwardTo,
            };

            if (autoDeleteOnIdle.HasValue)
            {
                subscriptionOptions.AutoDeleteOnIdle = autoDeleteOnIdle.Value;
            }

            var ruleOptions = new CreateRuleOptions(
                "TypeFilter",
                new SqlRuleFilter($"Subject = '{messageType}'"));

            await this.adminClient.CreateSubscriptionAsync(
                subscriptionOptions, ruleOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Subscription already exists — another pod or app created it first.
            // Verify the existing subscription forwards to the expected queue.
            // A mismatch indicates a naming collision — this is a configuration error.
            var existing = await this.adminClient.GetSubscriptionAsync(
                topicName, subscriptionName, cancellationToken);

            if (!string.Equals(existing.Value.ForwardTo, forwardTo, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Subscription '{subscriptionName}' on topic '{topicName}' already exists but " +
                    $"forwards to '{existing.Value.ForwardTo}' instead of expected '{forwardTo}'. " +
                    $"This indicates a naming collision between applications.");
            }
        }
    }
}
