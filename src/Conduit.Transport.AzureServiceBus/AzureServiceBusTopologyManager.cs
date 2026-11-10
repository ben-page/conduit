using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Manages Azure Service Bus topology: topics, queues, subscriptions.
/// Uses try-create with 409 conflict swallowing for idempotent startup across pods.
/// </summary>
internal sealed class AzureServiceBusTopologyManager
{
    private static readonly Action<ILogger, string, string, string, Exception?> LogSubscriptionRedirected =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, new EventId(200, "SubscriptionRedirected"),
            "Subscription '{Subscription}' forwarded to '{PreviousQueue}'; it now forwards to '{Queue}'");

    private readonly ServiceBusAdministrationClient adminClient;
    private readonly AzureServiceBusOptions options;
    private readonly ConduitOptions conduitOptions;
    private readonly ILogger logger;

    internal AzureServiceBusTopologyManager(
        ServiceBusAdministrationClient adminClient,
        AzureServiceBusOptions options,
        ConduitOptions conduitOptions,
        ILogger logger)
    {
        this.adminClient = adminClient;
        this.options = options;
        this.conduitOptions = conduitOptions;
        this.logger = logger;
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
        await this.TryCreateQueueAsync(this.CreateReplyQueueOptions(), cancellationToken);
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
        await this.TryCreateQueueAsync(CreateQueueOptions(appQueueName, ephemeralTtl), cancellationToken);

        // Reply queue with sessions enabled for request/response correlation
        await this.TryCreateQueueAsync(this.CreateReplyQueueOptions(), cancellationToken);

        // The queues this application's subscriptions forward to: a subscription forwarding to any of them
        // is this application's own, whichever of them its configuration names now
        var ownQueues = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { appQueueName };

        // Pool queues — must be created before subscriptions because
        // subscription ForwardTo targets are validated by ASB at creation time
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 260);
            await this.TryCreateQueueAsync(CreateQueueOptions(poolQueueName, ephemeralTtl), cancellationToken);
            ownQueues.Add(poolQueueName);
        }

        // Event subscriptions: multicast (per-app, includes app name)
        foreach (var eventType in subscribedEventTypes)
        {
            var subscription = new SubscriptionPlan(
                TopologyNaming.CreateAppResourceName(this.conduitOptions, eventType, 50),
                eventType,
                this.GetTargetQueueForType(eventType, appQueueName),
                AppScoped: true);
            await this.EnsureSubscriptionAsync(topicName, subscription, ownQueues, ephemeralTtl, cancellationToken);
        }

        // Command/query subscriptions: unicast (shared, no app name)
        foreach (var commandQueryType in handledCommandQueryTypes)
        {
            var subscription = new SubscriptionPlan(
                TopologyNaming.CreateSharedResourceName(this.conduitOptions, commandQueryType, 50),
                commandQueryType,
                this.GetTargetQueueForType(commandQueryType, appQueueName),
                AppScoped: false);
            await this.EnsureSubscriptionAsync(topicName, subscription, ownQueues, ephemeralTtl, cancellationToken);
        }
    }

    /// <summary>
    /// The reply queue every request of this application reads its reply from: session-enabled, each reply in
    /// its own request's session, and discarding a reply after <see cref="AzureServiceBusOptions.ReplyTimeToLive"/>
    /// by default (when there is one). A reply nobody has read by then belongs to a request that gave up, and
    /// would otherwise stay until the queue reaches its size quota and every reply fails.
    /// </summary>
    internal CreateQueueOptions CreateReplyQueueOptions()
    {
        var replyQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, "replies", 260);
        var queueOptions = CreateQueueOptions(replyQueueName, this.ComputeEphemeralTtl());
        queueOptions.RequiresSession = true;

        if (this.options.ReplyTimeToLive is { } timeToLive)
        {
            queueOptions.DefaultMessageTimeToLive = timeToLive;
        }

        return queueOptions;
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

    private static CreateQueueOptions CreateQueueOptions(string name, TimeSpan? autoDeleteOnIdle)
    {
        var queueOptions = new CreateQueueOptions(name);
        if (autoDeleteOnIdle.HasValue)
        {
            queueOptions.AutoDeleteOnIdle = autoDeleteOnIdle.Value;
        }

        return queueOptions;
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

    private async Task TryCreateQueueAsync(CreateQueueOptions queueOptions, CancellationToken cancellationToken)
    {
        try
        {
            await this.adminClient.CreateQueueAsync(queueOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Queue already exists — another pod created it first.
        }
    }

    /// <summary>
    /// Creates <paramref name="subscription"/>, or makes an existing one forward where this application's
    /// configuration says. An existing subscription that forwards elsewhere is redirected when it is this
    /// application's own: an event's, named for the application, or one forwarding to another of
    /// <paramref name="ownQueues"/>, as when a pool takes a type over or hands it back. One forwarding to any
    /// other queue is another application's, a naming collision: startup fails rather than take it over.
    /// </summary>
    private async Task EnsureSubscriptionAsync(
        string topicName,
        SubscriptionPlan subscription,
        HashSet<string> ownQueues,
        TimeSpan? autoDeleteOnIdle,
        CancellationToken cancellationToken)
    {
        try
        {
            var subscriptionOptions = new CreateSubscriptionOptions(topicName, subscription.Name)
            {
                ForwardTo = subscription.ForwardTo,
            };

            if (autoDeleteOnIdle.HasValue)
            {
                subscriptionOptions.AutoDeleteOnIdle = autoDeleteOnIdle.Value;
            }

            // ASB SQL filter uses sys.Label (not Subject) to match ServiceBusMessage.Subject
            var ruleOptions = new CreateRuleOptions(
                "TypeFilter",
                new SqlRuleFilter($"sys.Label = '{subscription.MessageType}'"));

            await this.adminClient.CreateSubscriptionAsync(
                subscriptionOptions, ruleOptions, cancellationToken);
        }
        catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityAlreadyExists)
        {
            // Subscription already exists — another pod or app created it first.
            SubscriptionProperties existing = await this.adminClient.GetSubscriptionAsync(
                topicName, subscription.Name, cancellationToken);

            // ASB normalizes ForwardTo to a full URI (e.g., "http://host/queue-name").
            // Compare by entity name only — extract the last path segment.
            var existingTarget = GetEntityName(existing.ForwardTo);
            if (string.Equals(existingTarget, subscription.ForwardTo, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!subscription.AppScoped && !ownQueues.Contains(existingTarget))
            {
                throw new InvalidOperationException(
                    $"Subscription '{subscription.Name}' on topic '{topicName}' already exists but " +
                    $"forwards to '{existing.ForwardTo}' instead of expected '{subscription.ForwardTo}'. " +
                    "This indicates a naming collision between applications. If that queue belonged to this " +
                    "application (a pool it no longer has), delete the subscription.");
            }

            existing.ForwardTo = subscription.ForwardTo;
            await this.adminClient.UpdateSubscriptionAsync(existing, cancellationToken);
            LogSubscriptionRedirected(this.logger, subscription.Name, existingTarget, subscription.ForwardTo, null);
        }
    }

    /// <summary>
    /// Extracts the entity name from a ForwardTo value, which may be a short name
    /// or a full URI (ASB normalizes to full URI on the server side).
    /// </summary>
    private static string GetEntityName(string forwardTo)
    {
        if (Uri.TryCreate(forwardTo, UriKind.Absolute, out var uri))
        {
            return uri.AbsolutePath.TrimStart('/');
        }

        return forwardTo;
    }

    /// <summary>
    /// A subscription this application needs: its name, the message type its filter matches, the queue it
    /// forwards to, and whether its name is this application's alone (an event's) rather than shared by every
    /// application (a command's or query's).
    /// </summary>
    private sealed record SubscriptionPlan(string Name, string MessageType, string ForwardTo, bool AppScoped);
}
