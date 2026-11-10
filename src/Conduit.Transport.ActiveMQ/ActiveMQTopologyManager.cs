using System.Text.Json;
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
/// - Multicast address for events: each subscribing app gets its own queue (and one per pool that
///   takes events), filtered to the event types it takes, so every app gets a copy of exactly those.
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

    private static readonly Action<ILogger, string, Exception?> LogUpdatingQueueFilter =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(203, "UpdatingQueueFilter"),
            "Updating the filter of existing Artemis queue '{QueueName}'");

    private static readonly TimeSpan ManagementTimeout = TimeSpan.FromSeconds(10);

    private static readonly QueueLifetime ProductionLifetime =
        new(Durable: true, AutoDelete: false, AutoDeleteDelayMilliseconds: 0, AutoDeleteMessageCount: 0);

    private readonly ActiveMQOptions options;
    private readonly ConduitOptions conduitOptions;
    private readonly ILogger logger;

    public ActiveMQTopologyManager(ActiveMQOptions options, ConduitOptions conduitOptions, ILogger logger)
    {
        this.options = options;
        this.conduitOptions = conduitOptions;
        this.logger = logger;
    }

    /// <summary>The anycast address commands and queries are sent to.</summary>
    public string AnycastAddress => TopologyNaming.CreateSharedResourceName(this.conduitOptions, "commands", 256);

    /// <summary>The multicast address events are published to.</summary>
    public string MulticastAddress => TopologyNaming.CreateSharedResourceName(this.conduitOptions, "events", 256);

    /// <summary>
    /// The fully qualified name (<c>address::queue</c>) of the queue this application takes
    /// <paramref name="messageType"/> from, for a redelivery that must reach this application's queue
    /// rather than every subscriber: a command or query's shared queue, an event's own app queue, or the
    /// pool queue either kind belongs to. Each queue's filter admits the type, so a message sent there by
    /// this name is not dropped, as one sent to a queue whose filter leaves its type out would be.
    /// </summary>
    public string RedeliveryQueue(string messageType, bool isCommandOrQuery)
    {
        var pool = this.options.Pools.FirstOrDefault(
            candidate => candidate.MessageTypes.Contains(messageType, StringComparer.Ordinal));

        if (isCommandOrQuery)
        {
            var commandQueueName = TopologyNaming.CreateSharedResourceName(
                this.conduitOptions, pool is null ? messageType : $"{pool.Name}.{messageType}", 256);
            return $"{this.AnycastAddress}::{commandQueueName}";
        }

        var eventQueueName = pool is null
            ? TopologyNaming.CreateAppResourceName(this.conduitOptions, 256)
            : TopologyNaming.CreateAppResourceName(this.conduitOptions, pool.Name, 256);
        return $"{this.MulticastAddress}::{eventQueueName}";
    }

    /// <summary>
    /// Creates only the addresses needed for publishing messages. Does not create
    /// inbound queues or filters. Called when <see cref="ConduitOptions.ProducerOnly"/> is true.
    /// </summary>
    public async Task<(string AnycastAddress, string MulticastAddress)> EnsureAddressesAsync(
        Session session, CancellationToken cancellationToken)
    {
        var anycastAddress = this.AnycastAddress;
        var multicastAddress = this.MulticastAddress;

        var (sender, receiver, replyAddress) = await OpenManagementLinksAsync(session, cancellationToken);
        try
        {
            await this.CreateAddressAsync(sender, receiver, replyAddress, anycastAddress, "ANYCAST", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            await this.CreateAddressAsync(sender, receiver, replyAddress, multicastAddress, "MULTICAST", cancellationToken);
        }
        finally
        {
            await CloseManagementLinksAsync(sender, receiver);
        }

        return (anycastAddress, multicastAddress);
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
        var anycastAddress = this.AnycastAddress;
        var multicastAddress = this.MulticastAddress;

        // Open management links once — reuse for all operations
        var (sender, receiver, replyAddress) = await OpenManagementLinksAsync(session, cancellationToken);
        try
        {
            return await this.EnsureTopologyCoreAsync(
                sender, receiver, replyAddress,
                anycastAddress, multicastAddress,
                subscribedEventTypes, handledCommandQueryTypes, cancellationToken);
        }
        finally
        {
            await CloseManagementLinksAsync(sender, receiver);
        }
    }

    private async Task<TopologyResult> EnsureTopologyCoreAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress,
        string anycastAddress, string multicastAddress,
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        // Create the two addresses
        await this.CreateAddressAsync(sender, receiver, replyAddress, anycastAddress, "ANYCAST", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        await this.CreateAddressAsync(sender, receiver, replyAddress, multicastAddress, "MULTICAST", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        // Collect pool-assigned types
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
                sender, receiver, replyAddress,
                anycastAddress, queueName, "ANYCAST",
                CommandQueueFilter(commandQueryType),
                updateFilterIfExists: false,
                cancellationToken);

            sharedCommandQueryQueues.Add(queueName);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // The per-app event queue (multicast — each app gets its own copy). Artemis hands every message
        // on a multicast address to every queue bound to it, so the filter is what keeps out the event
        // types other apps subscribe and the ones this app's pools take.
        var appEventQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 256);
        var appEventTypes = subscribedEventTypes
            .Where(eventType => !poolAssignedTypes.Contains(eventType))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var hasAppEventQueue = appEventTypes.Count > 0;

        if (hasAppEventQueue)
        {
            await this.CreateQueueAsync(
                sender, receiver, replyAddress,
                multicastAddress, appEventQueueName, "MULTICAST",
                EventQueueFilter(appEventTypes),
                updateFilterIfExists: true,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Pool queues
        var commandQuerySet = new HashSet<string>(handledCommandQueryTypes, StringComparer.Ordinal);
        var subscribedEventSet = new HashSet<string>(subscribedEventTypes, StringComparer.Ordinal);
        var poolEventQueues = new List<(string QueueName, int PrefetchCount)>();
        var poolCommandQueryQueues = new List<(string QueueName, int PrefetchCount)>();

        foreach (var pool in this.options.Pools)
        {
            foreach (var messageType in pool.MessageTypes.Where(commandQuerySet.Contains))
            {
                var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
                    this.conduitOptions, $"{pool.Name}.{messageType}", 256);

                await this.CreateQueueAsync(
                    sender, receiver, replyAddress,
                    anycastAddress, sharedPoolQueueName, "ANYCAST",
                    CommandQueueFilter(messageType),
                    updateFilterIfExists: false,
                    cancellationToken);

                poolCommandQueryQueues.Add((sharedPoolQueueName, pool.Concurrency));
                cancellationToken.ThrowIfCancellationRequested();
            }

            // One queue, and one consumer, for the pool's events: those of its types this app subscribes
            var poolEventTypes = pool.MessageTypes.Where(subscribedEventSet.Contains).ToList();
            if (poolEventTypes.Count > 0)
            {
                var poolEventQueueName = TopologyNaming.CreateAppResourceName(
                    this.conduitOptions, pool.Name, 256);

                await this.CreateQueueAsync(
                    sender, receiver, replyAddress,
                    multicastAddress, poolEventQueueName, "MULTICAST",
                    EventQueueFilter(poolEventTypes),
                    updateFilterIfExists: true,
                    cancellationToken);

                poolEventQueues.Add((poolEventQueueName, pool.Concurrency));
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

    private async Task CreateAddressAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress,
        string addressName, string routingType,
        CancellationToken cancellationToken)
    {
        LogCreatingAddress(this.logger, addressName, null);

        try
        {
            var body = JsonSerializer.Serialize(new object[] { addressName, routingType });
            using var request = CreateManagementRequest("createAddress", body);

            await SendManagementRequestAsync(sender, receiver, replyAddress, request, cancellationToken);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ActiveMQAddressExistsException", StringComparison.OrdinalIgnoreCase))
#pragma warning restore CA1031
        {
            LogResourceAlreadyExists(this.logger, addressName, null);
        }
    }

    /// <summary>
    /// How long the queues of <paramref name="topologyMode"/> live. Production queues are durable and
    /// never auto-deleted. Developer and Test queues are not durable, and Artemis deletes one once it has
    /// had no consumer for 4 hours (Developer) or 5 minutes (Test), whatever it still holds
    /// (<c>autoDeleteMessageCount</c> -1). A queue that never had a consumer is never auto-deleted.
    /// </summary>
    internal static QueueLifetime QueueLifetimeFor(TopologyMode topologyMode) => topologyMode switch
    {
        TopologyMode.Developer => new QueueLifetime(
            Durable: false, AutoDelete: true, (long)TimeSpan.FromHours(4).TotalMilliseconds, AutoDeleteMessageCount: -1),
        TopologyMode.Test => new QueueLifetime(
            Durable: false, AutoDelete: true, (long)TimeSpan.FromMinutes(5).TotalMilliseconds, AutoDeleteMessageCount: -1),
        TopologyMode.Production => ProductionLifetime,
        _ => ProductionLifetime,
    };

    /// <summary>
    /// The name of a new consumer receiver link: unique, and independent of the queue's name, which is
    /// as short as a system and application name or as long as 256 characters.
    /// </summary>
    internal static string ConsumerLinkName() => LinkName("consumer");

    /// <summary>
    /// A unique name for a new link playing <paramref name="role"/>: no two links on a connection, nor on
    /// the connection a reconnection replaces it with, share a name.
    /// </summary>
    internal static string LinkName(string role) => $"{role}-{Guid.NewGuid():N}";

    /// <summary>
    /// The filter of a command or query queue: the one type it is named after.
    /// </summary>
    internal static string CommandQueueFilter(string messageType) =>
        $"JMSType={FilterLiteral(messageType)}";

    /// <summary>
    /// The filter of an event queue on the multicast address: the event types it takes, and no other.
    /// </summary>
    internal static string EventQueueFilter(IEnumerable<string> eventTypes) =>
        $"JMSType IN ({string.Join(',', eventTypes.Select(FilterLiteral))})";

    // A filter string literal: single-quoted, an embedded quote doubled
    private static string FilterLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private async Task CreateQueueAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress,
        string addressName, string queueName, string routingType,
        string filterExpression,
        bool updateFilterIfExists,
        CancellationToken cancellationToken)
    {
        LogCreatingQueue(this.logger, queueName, null);

        try
        {
            var lifetime = QueueLifetimeFor(this.conduitOptions.TopologyMode);

            // Artemis's management operations take positional parameters, and it picks the createQueue
            // overload by their count: these 19 are the overload that carries the auto-delete settings.
            // Past purgeOnNoConsumers, every parameter but autoDelete, autoDeleteDelay and
            // autoDeleteMessageCount is the broker's own default.
            var body = JsonSerializer.Serialize(new object?[]
            {
                addressName,                        // address
                routingType,                        // routingType (ANYCAST or MULTICAST)
                queueName,                          // name
                filterExpression,                   // filter
                lifetime.Durable,                   // durable
                -1,                                 // maxConsumers (-1 = unlimited)
                false,                              // purgeOnNoConsumers
                false,                              // exclusive
                false,                              // groupRebalance
                -1,                                 // groupBuckets
                false,                              // lastValue
                null,                               // lastValueKey
                false,                              // nonDestructive
                0,                                  // consumersBeforeDispatch
                -1L,                                // delayBeforeDispatch
                lifetime.AutoDelete,                // autoDelete
                lifetime.AutoDeleteDelayMilliseconds, // autoDeleteDelay
                lifetime.AutoDeleteMessageCount,    // autoDeleteMessageCount
                false,                              // autoCreateAddress
            });

            using var request = CreateManagementRequest("createQueue", body);

            await SendManagementRequestAsync(sender, receiver, replyAddress, request, cancellationToken);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ActiveMQQueueExistsException", StringComparison.OrdinalIgnoreCase))
#pragma warning restore CA1031
        {
            LogResourceAlreadyExists(this.logger, queueName, null);

            if (updateFilterIfExists)
            {
                await this.UpdateQueueFilterAsync(sender, receiver, replyAddress, queueName, filterExpression, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Sets an existing event queue's filter to the event types this release subscribes: an event queue
    /// created by an earlier release keeps the filter of that release's types otherwise, and a type added
    /// since would never reach it. A partial <c>updateQueue</c> leaves every field it does not name alone,
    /// except the filter, which it would clear.
    /// </summary>
    private async Task UpdateQueueFilterAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress,
        string queueName, string filterExpression,
        CancellationToken cancellationToken)
    {
        LogUpdatingQueueFilter(this.logger, queueName, null);

        var configuration = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["name"] = queueName,
            ["filter-string"] = filterExpression,
        });

        using var request = CreateManagementRequest("updateQueue", JsonSerializer.Serialize(new[] { configuration }));
        await SendManagementRequestAsync(sender, receiver, replyAddress, request, cancellationToken);
    }

    /// <summary>
    /// Opens a reusable management sender/receiver link pair. Call <see cref="CloseManagementLinksAsync"/>
    /// when all operations are complete. Reusing links avoids the AMQP attach/detach handshake
    /// per request, which dominates topology creation time.
    /// </summary>
    private static async Task<(SenderLink Sender, ReceiverLink Receiver, string ReplyAddress)>
        OpenManagementLinksAsync(Session session, CancellationToken cancellationToken)
    {
        var replyAddressTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiverLink = new ReceiverLink(
            session,
            $"mgmt-recv-{Guid.NewGuid():N}",
            new Source { Dynamic = true },
            (link, attach) =>
            {
                if (attach.Source is Source source && source.Address is not null)
                {
                    replyAddressTcs.TrySetResult(source.Address);
                }
                else
                {
                    replyAddressTcs.TrySetException(
                        new InvalidOperationException("Broker did not assign a dynamic management reply queue."));
                }
            });

        var replyAddress = await replyAddressTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var senderLink = new SenderLink(session, $"mgmt-send-{Guid.NewGuid():N}", "activemq.management");

        return (senderLink, receiverLink, replyAddress);
    }

    /// <summary>
    /// Detaches the management links and waits for the broker's answer, at most 10 seconds each (a broker
    /// that stopped answering would otherwise hold each close for AMQPNetLite's default minute). Do not
    /// stop waiting: with the detaches still unanswered when the sender links were attached on the same
    /// session, a request sent right after a reconnection never reached its queue (Artemis 2.44,
    /// <c>ActiveMQReconnectShould.ResumeAfterBrokerRestart</c>: six failures in seven runs, none since).
    /// </summary>
    private static async Task CloseManagementLinksAsync(SenderLink sender, ReceiverLink receiver)
    {
        foreach (var link in new Link[] { sender, receiver })
        {
            try
            {
                if (!link.IsClosed)
                {
                    await link.CloseAsync(ManagementTimeout);
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

    /// <summary>
    /// Sends a management request using pre-opened links. The send and the wait for the answer are each
    /// bounded by 10 seconds (AMQPNetLite's own send timeout, which withdraws the message, is otherwise a
    /// minute), and the wait also by <paramref name="cancellationToken"/>: a broker still starting can
    /// accept a connection and then answer nothing on it.
    /// </summary>
    private static async Task SendManagementRequestAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress, Message request,
        CancellationToken cancellationToken)
    {
        request.Properties.ReplyTo = replyAddress;
        request.Properties.MessageId = $"mgmt-{Guid.NewGuid():N}";
        var operation = request.ApplicationProperties?["_AMQ_OperationName"];

        await sender.SendAsync(request, ManagementTimeout);

        var reply = await receiver.ReceiveAsync(ManagementTimeout).WaitAsync(cancellationToken);
        if (reply is null)
        {
            throw new TimeoutException(
                $"ActiveMQ management request timed out after {ManagementTimeout.TotalSeconds} seconds. Operation: {operation}");
        }

        receiver.Accept(reply);

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

    /// <summary>
    /// Creates a management request message. The <paramref name="jsonBody"/> contains
    /// the operation parameters as a JSON array (e.g., <c>["addressName","ANYCAST"]</c>
    /// for createAddress, or a nested JSON config string for createQueue).
    /// </summary>
    private static Message CreateManagementRequest(string operation, string jsonBody)
    {
        // new Message(string) wraps the body in an AmqpValue section automatically
        return new Message(jsonBody)
        {
            Properties = new Properties(),
            ApplicationProperties = new ApplicationProperties
            {
                ["_AMQ_ResourceName"] = "broker",
                ["_AMQ_OperationName"] = operation,
            },
        };
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

/// <summary>
/// The durability and auto-delete settings a queue is created with (<see cref="ActiveMQTopologyManager.QueueLifetimeFor"/>).
/// </summary>
/// <param name="Durable">Whether the queue and its messages survive a broker restart.</param>
/// <param name="AutoDelete">Whether Artemis deletes the queue once it has had no consumer for the delay.</param>
/// <param name="AutoDeleteDelayMilliseconds">How long the queue must have had no consumer.</param>
/// <param name="AutoDeleteMessageCount">The most messages the queue may hold and still be deleted; -1 for any number.</param>
internal readonly record struct QueueLifetime(
    bool Durable,
    bool AutoDelete,
    long AutoDeleteDelayMilliseconds,
    long AutoDeleteMessageCount);
