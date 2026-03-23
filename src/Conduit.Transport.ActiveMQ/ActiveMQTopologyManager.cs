using Amqp;
using Amqp.Framing;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// Creates Artemis addresses and queues via the management address (<c>activemq.management</c>).
/// All operations are idempotent — if the resource already exists, the call is a no-op.
/// </summary>
/// <remarks>
/// Uses two addresses:
/// - Anycast address for commands/queries: each command/query type gets its own queue
///   with a filter, and Artemis delivers to exactly one consumer (competing consumers).
/// - Multicast address for events: each subscribing app gets its own queue per event type,
///   so every app gets a copy (multicast).
/// </remarks>
internal sealed class ActiveMQTopologyManager
{
    private static readonly Action<ILogger, string, Exception?> LogCreatingAddress =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(200, "CreatingAddress"),
            "Creating Artemis address '{AddressName}'");

    private static readonly Action<ILogger, string, Exception?> LogCreatingQueue =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(201, "CreatingQueue"),
            "Creating Artemis queue '{QueueName}'");

    private static readonly Action<ILogger, string, Exception?> LogResourceAlreadyExists =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(202, "ResourceAlreadyExists"),
            "Artemis resource '{Name}' already exists");

    private readonly ActiveMQOptions options;
    private readonly ConduitOptions conduitOptions;
    private readonly ILogger logger;

    public ActiveMQTopologyManager(ActiveMQOptions options, ConduitOptions conduitOptions, ILogger logger)
    {
        this.options = options;
        this.conduitOptions = conduitOptions;
        this.logger = logger;
    }

    /// <summary>
    /// Ensures all required topology exists: anycast address for commands/queries,
    /// multicast address for events, per-type queues with filters, and pool queues.
    /// Returns the topology result with queue names and their associated prefetch counts.
    /// </summary>
    public async Task<TopologyResult> EnsureTopologyAsync(
        Session session,
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        var anycastAddress = TopologyNaming.CreateSharedResourceName(this.conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(this.conduitOptions, "events", 256);

        // Create the two addresses
        await this.CreateAddressAsync(session, anycastAddress, "ANYCAST");
        cancellationToken.ThrowIfCancellationRequested();

        await this.CreateAddressAsync(session, multicastAddress, "MULTICAST");
        cancellationToken.ThrowIfCancellationRequested();

        // Collect pool-assigned types so they are only bound to their pool queue, not the default queue
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in this.options.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        // Create shared command/query queues (unicast — competing consumers across apps)
        var sharedCommandQueryQueues = new List<string>();

        foreach (var commandQueryType in handledCommandQueryTypes)
        {
            if (poolAssignedTypes.Contains(commandQueryType))
            {
                continue;
            }

            var queueName = TopologyNaming.CreateSharedResourceName(
                this.conduitOptions, commandQueryType, 256);

            await this.CreateQueueAsync(
                session, anycastAddress, queueName, "ANYCAST",
                filterExpression: $"AMQPSubject='{commandQueryType}'");

            sharedCommandQueryQueues.Add(queueName);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Create per-app event queues (multicast — each app gets its own copy)
        var appEventQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 256);
        var hasAppEventQueue = false;

        foreach (var eventType in subscribedEventTypes)
        {
            if (poolAssignedTypes.Contains(eventType))
            {
                continue;
            }

            if (!hasAppEventQueue)
            {
                // Create the app-level event queue on the multicast address (no filter — receives all events)
                await this.CreateQueueAsync(
                    session, multicastAddress, appEventQueueName, "MULTICAST",
                    filterExpression: null);
                hasAppEventQueue = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Pool queues — events use per-app pool queues (multicast),
        // commands/queries use shared pool queues (unicast)
        var commandQuerySet = new HashSet<string>(handledCommandQueryTypes, StringComparer.Ordinal);
        var poolEventQueues = new List<(string QueueName, int PrefetchCount)>();
        var poolCommandQueryQueues = new List<(string QueueName, int PrefetchCount)>();

        foreach (var pool in this.options.Pools)
        {
            var hasPoolEvents = false;
            var poolEventQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 256);

            foreach (var messageType in pool.MessageTypes)
            {
                if (commandQuerySet.Contains(messageType))
                {
                    // Command/query in pool -> shared pool queue (unicast — competing consumers)
                    var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
                        this.conduitOptions, $"{pool.Name}.{messageType}", 256);

                    await this.CreateQueueAsync(
                        session, anycastAddress, sharedPoolQueueName, "ANYCAST",
                        filterExpression: $"AMQPSubject='{messageType}'");

                    poolCommandQueryQueues.Add((sharedPoolQueueName, pool.PrefetchCount));
                }
                else
                {
                    // Event in pool -> per-app pool queue (multicast)
                    if (!hasPoolEvents)
                    {
                        await this.CreateQueueAsync(
                            session, multicastAddress, poolEventQueueName, "MULTICAST",
                            filterExpression: null);
                        hasPoolEvents = true;
                    }

                    poolEventQueues.Add((poolEventQueueName, pool.PrefetchCount));
                }

                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        return new TopologyResult(
            anycastAddress,
            multicastAddress,
            appEventQueueName,
            hasAppEventQueue,
            sharedCommandQueryQueues,
            poolEventQueues,
            poolCommandQueryQueues);
    }

    private async Task CreateAddressAsync(Session session, string addressName, string routingType)
    {
        LogCreatingAddress(this.logger, addressName, null);

        try
        {
            using var request = CreateManagementRequest("createAddress");
            request.ApplicationProperties["name"] = addressName;
            request.ApplicationProperties["routingType"] = routingType;

            await SendManagementRequestAsync(session, request);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ActiveMQAddressExistsException", StringComparison.OrdinalIgnoreCase))
#pragma warning restore CA1031
        {
            LogResourceAlreadyExists(this.logger, addressName, null);
        }
    }

    private async Task CreateQueueAsync(
        Session session,
        string addressName,
        string queueName,
        string routingType,
        string? filterExpression)
    {
        LogCreatingQueue(this.logger, queueName, null);

        try
        {
            using var request = CreateManagementRequest("createQueue");
            request.ApplicationProperties["address"] = addressName;
            request.ApplicationProperties["name"] = queueName;
            request.ApplicationProperties["routingType"] = routingType;
            request.ApplicationProperties["durable"] = this.conduitOptions.TopologyMode == TopologyMode.Production;

            if (filterExpression is not null)
            {
                request.ApplicationProperties["filter"] = filterExpression;
            }

            if (this.conduitOptions.TopologyMode != TopologyMode.Production)
            {
                var autoDeleteDelayMs = this.conduitOptions.TopologyMode switch
                {
                    TopologyMode.Production => 0L,
                    TopologyMode.Developer => (long)TimeSpan.FromHours(4).TotalMilliseconds,
                    TopologyMode.Test => (long)TimeSpan.FromMinutes(5).TotalMilliseconds,
                    _ => 0L,
                };

                if (autoDeleteDelayMs > 0)
                {
                    request.ApplicationProperties["autoDelete"] = true;
                    request.ApplicationProperties["autoDeleteDelay"] = autoDeleteDelayMs;
                }
            }

            await SendManagementRequestAsync(session, request);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ActiveMQQueueExistsException", StringComparison.OrdinalIgnoreCase))
#pragma warning restore CA1031
        {
            LogResourceAlreadyExists(this.logger, queueName, null);
        }
    }

    private static async Task SendManagementRequestAsync(Session session, Message request)
    {
        var managementAddress = "activemq.management";
        var replyAddress = $"management-reply-{Guid.NewGuid():N}";

        // Set reply-to for the management response
        request.Properties.ReplyTo = replyAddress;
        request.Properties.MessageId = $"mgmt-{Guid.NewGuid():N}";

        // Create a temporary receiver for the reply
        var replySource = new Source { Address = replyAddress };
        var receiverLink = new ReceiverLink(
            session,
            $"mgmt-recv-{Guid.NewGuid():N}",
            replySource,
            null);

        var senderLink = new SenderLink(session, $"mgmt-send-{Guid.NewGuid():N}", managementAddress);

        try
        {
            await senderLink.SendAsync(request);

            var reply = await receiverLink.ReceiveAsync(TimeSpan.FromSeconds(10));
            if (reply is not null)
            {
                receiverLink.Accept(reply);

                // Check for errors in the management response
                if (reply.ApplicationProperties is not null &&
                    reply.ApplicationProperties.Map.ContainsKey("_AMQ_OperationSucceeded"))
                {
                    var succeeded = reply.ApplicationProperties["_AMQ_OperationSucceeded"];
                    if (succeeded is bool success && !success)
                    {
                        var errorBody = reply.Body?.ToString() ?? "Unknown management error";
                        throw new InvalidOperationException(errorBody);
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (!senderLink.IsClosed)
                {
                    await senderLink.CloseAsync();
                }
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }

            try
            {
                if (!receiverLink.IsClosed)
                {
                    await receiverLink.CloseAsync();
                }
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Best-effort cleanup
            }
        }
    }

    private static Message CreateManagementRequest(string operation)
    {
        var message = new Message
        {
            Properties = new Properties(),
            ApplicationProperties = new ApplicationProperties
            {
                ["_AMQ_ResourceName"] = "broker",
                ["_AMQ_OperationName"] = operation,
            },
        };
        return message;
    }
}

/// <summary>
/// Result of topology setup containing all address/queue names that need consumers.
/// </summary>
internal sealed class TopologyResult
{
    /// <summary>
    /// The anycast address for commands and queries.
    /// </summary>
    public string AnycastAddress { get; }

    /// <summary>
    /// The multicast address for events.
    /// </summary>
    public string MulticastAddress { get; }

    /// <summary>
    /// The per-app event queue name on the multicast address.
    /// </summary>
    public string AppEventQueueName { get; }

    /// <summary>
    /// Whether the app event queue was created (i.e. there are subscribed event types).
    /// </summary>
    public bool HasAppEventQueue { get; }

    /// <summary>
    /// Shared command/query queue names (unicast, default prefetch count).
    /// </summary>
    public IReadOnlyList<string> SharedCommandQueryQueues { get; }

    /// <summary>
    /// Pool event queue names with their pool-specific prefetch counts (multicast, per-app).
    /// </summary>
    public IReadOnlyList<(string QueueName, int PrefetchCount)> PoolEventQueues { get; }

    /// <summary>
    /// Pool command/query queue names with their pool-specific prefetch counts (unicast, shared).
    /// </summary>
    public IReadOnlyList<(string QueueName, int PrefetchCount)> PoolCommandQueryQueues { get; }

    public TopologyResult(
        string anycastAddress,
        string multicastAddress,
        string appEventQueueName,
        bool hasAppEventQueue,
        IReadOnlyList<string> sharedCommandQueryQueues,
        IReadOnlyList<(string QueueName, int PrefetchCount)> poolEventQueues,
        IReadOnlyList<(string QueueName, int PrefetchCount)> poolCommandQueryQueues)
    {
        this.AnycastAddress = anycastAddress;
        this.MulticastAddress = multicastAddress;
        this.AppEventQueueName = appEventQueueName;
        this.HasAppEventQueue = hasAppEventQueue;
        this.SharedCommandQueryQueues = sharedCommandQueryQueues;
        this.PoolEventQueues = poolEventQueues;
        this.PoolCommandQueryQueues = poolCommandQueryQueues;
    }
}
