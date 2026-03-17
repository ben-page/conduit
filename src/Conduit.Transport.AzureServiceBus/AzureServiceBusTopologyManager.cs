using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Manages Azure Service Bus topology: topics, queues, subscriptions.
/// Uses try-create with 409 conflict swallowing for idempotent startup across pods.
/// </summary>
internal sealed class AzureServiceBusTopologyManager
{
    private readonly ServiceBusAdministrationClient adminClient;
    private readonly AzureServiceBusOptions options;
    private readonly string applicationName;

    internal AzureServiceBusTopologyManager(
        ServiceBusAdministrationClient adminClient,
        AzureServiceBusOptions options,
        string applicationName)
    {
        this.adminClient = adminClient;
        this.options = options;
        this.applicationName = applicationName;
    }

    /// <summary>
    /// Creates all required topology for the transport: topic, app queue, reply queue,
    /// event subscriptions with forwarding rules, and pool queues.
    /// </summary>
    public async Task EnsureTopologyAsync(
        IReadOnlyList<string> subscribedEventTypes,
        CancellationToken cancellationToken)
    {
        // Shared event topic
        await this.TryCreateTopicAsync(this.options.TopicName, cancellationToken);

        // Default app queue for receiving events forwarded from subscriptions
        var queueName = this.GetQueueName(this.applicationName);
        await this.TryCreateQueueAsync(queueName, requiresSession: false, cancellationToken);

        // Reply queue with sessions enabled for request/response correlation
        var replyQueueName = $"{queueName}-replies";
        await this.TryCreateQueueAsync(replyQueueName, requiresSession: true, cancellationToken);

        // Create subscriptions for each event type this app listens for,
        // with SQL filter on Subject and forwarding to the app queue
        foreach (var eventType in subscribedEventTypes)
        {
            var targetQueue = this.GetTargetQueueForType(eventType, queueName);
            await this.TryCreateSubscriptionAsync(
                this.options.TopicName, eventType, targetQueue, cancellationToken);
        }

        // Pool queues with separate processors
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = $"{queueName}-{pool.Name}";
            await this.TryCreateQueueAsync(poolQueueName, requiresSession: false, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a command queue for an endpoint (used by senders routing commands
    /// directly to a service's queue, bypassing the topic).
    /// </summary>
    public async Task EnsureCommandQueueAsync(string endpointName, CancellationToken cancellationToken)
    {
        var commandQueueName = GetCommandQueueName(endpointName);
        await this.TryCreateQueueAsync(commandQueueName, requiresSession: false, cancellationToken);
    }

    /// <summary>
    /// Gets the app receive queue name for the given application.
    /// </summary>
    internal string GetQueueName(string appName)
    {
        return this.options.QueuePrefix is not null
            ? $"{this.options.QueuePrefix}-{appName}"
            : $"conduit-{appName}";
    }

    /// <summary>
    /// Gets the command queue name for the given endpoint.
    /// </summary>
    internal static string GetCommandQueueName(string endpointName) => $"conduit-{endpointName}";

    /// <summary>
    /// Gets the reply queue name for the given application.
    /// </summary>
    internal string GetReplyQueueName(string appName) => $"{this.GetQueueName(appName)}-replies";

    private string GetTargetQueueForType(string eventType, string defaultQueueName)
    {
        foreach (var pool in this.options.Pools)
        {
            if (pool.MessageTypes.Contains(eventType, StringComparer.Ordinal))
            {
                return $"{defaultQueueName}-{pool.Name}";
            }
        }

        return defaultQueueName;
    }

    private async Task TryCreateTopicAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await this.adminClient.CreateTopicAsync(name, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Topic already exists — another pod created it first.
        }
    }

    private async Task TryCreateQueueAsync(string name, bool requiresSession, CancellationToken cancellationToken)
    {
        try
        {
            var queueOptions = new CreateQueueOptions(name)
            {
                RequiresSession = requiresSession,
            };
            await this.adminClient.CreateQueueAsync(queueOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Queue already exists — another pod created it first.
        }
    }

    private async Task TryCreateSubscriptionAsync(
        string topicName,
        string messageType,
        string forwardTo,
        CancellationToken cancellationToken)
    {
        // Subscription names have a 50-char limit. Use a safe, deterministic name.
        var subscriptionName = SanitizeSubscriptionName(messageType);

        try
        {
            var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                ForwardTo = forwardTo,
            };

            var ruleOptions = new CreateRuleOptions(
                "TypeFilter",
                new SqlRuleFilter($"Subject = '{messageType}'"));

            await this.adminClient.CreateSubscriptionAsync(subscriptionOptions, ruleOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Subscription already exists — another pod created it first.
        }
    }

    /// <summary>
    /// Azure Service Bus subscription names are limited to 50 characters and must be
    /// alphanumeric, hyphens, underscores, or periods. Replace dots with hyphens and truncate.
    /// </summary>
    private static string SanitizeSubscriptionName(string messageType)
    {
        var sanitized = messageType.Replace('.', '-');
        return sanitized.Length <= 50
            ? sanitized
            : sanitized[..50];
    }
}
