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
    /// Creates only the addresses needed for publishing messages. Does not create
    /// inbound queues or filters. Called when <see cref="ConduitOptions.ProducerOnly"/> is true.
    /// </summary>
    public async Task<(string AnycastAddress, string MulticastAddress)> EnsureAddressesAsync(
        Session session, CancellationToken cancellationToken)
    {
        var anycastAddress = TopologyNaming.CreateSharedResourceName(this.conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(this.conduitOptions, "events", 256);

        var (sender, receiver, replyAddress) = await OpenManagementLinksAsync(session);
        try
        {
            await this.CreateAddressAsync(sender, receiver, replyAddress, anycastAddress, "ANYCAST");
            cancellationToken.ThrowIfCancellationRequested();

            await this.CreateAddressAsync(sender, receiver, replyAddress, multicastAddress, "MULTICAST");
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
        var anycastAddress = TopologyNaming.CreateSharedResourceName(this.conduitOptions, "commands", 256);
        var multicastAddress = TopologyNaming.CreateSharedResourceName(this.conduitOptions, "events", 256);

        // Open management links once — reuse for all operations
        var (sender, receiver, replyAddress) = await OpenManagementLinksAsync(session);
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
        await this.CreateAddressAsync(sender, receiver, replyAddress, anycastAddress, "ANYCAST");
        cancellationToken.ThrowIfCancellationRequested();

        await this.CreateAddressAsync(sender, receiver, replyAddress, multicastAddress, "MULTICAST");
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
                filterExpression: $"JMSType='{commandQueryType}'");

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
                await this.CreateQueueAsync(
                    sender, receiver, replyAddress,
                    multicastAddress, appEventQueueName, "MULTICAST",
                    filterExpression: null);
                hasAppEventQueue = true;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // Pool queues
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
                    var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
                        this.conduitOptions, $"{pool.Name}.{messageType}", 256);

                    await this.CreateQueueAsync(
                        sender, receiver, replyAddress,
                        anycastAddress, sharedPoolQueueName, "ANYCAST",
                        filterExpression: $"JMSType='{messageType}'");

                    poolCommandQueryQueues.Add((sharedPoolQueueName, pool.Concurrency));
                }
                else
                {
                    if (!hasPoolEvents)
                    {
                        await this.CreateQueueAsync(
                            sender, receiver, replyAddress,
                            multicastAddress, poolEventQueueName, "MULTICAST",
                            filterExpression: null);
                        hasPoolEvents = true;
                    }

                    poolEventQueues.Add((poolEventQueueName, pool.Concurrency));
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

    private async Task CreateAddressAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress,
        string addressName, string routingType)
    {
        LogCreatingAddress(this.logger, addressName, null);

        try
        {
            var body = JsonSerializer.Serialize(new object[] { addressName, routingType });
            using var request = CreateManagementRequest("createAddress", body);

            await SendManagementRequestAsync(sender, receiver, replyAddress, request);
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
        SenderLink sender, ReceiverLink receiver, string replyAddress,
        string addressName, string queueName, string routingType,
        string? filterExpression)
    {
        LogCreatingQueue(this.logger, queueName, null);

        try
        {
            var isDurable = this.conduitOptions.TopologyMode == TopologyMode.Production;

            // Artemis createQueue uses positional parameters (NOT a JSON config object).
            // The JSON config format silently defaults to multicast with no filter.
            // Positional: [address, routingType, name, filter, durable, maxConsumers,
            //              purgeOnNoConsumers, autoCreateAddress]
            var body = JsonSerializer.Serialize(new object?[]
            {
                addressName,                   // address
                routingType,                   // routingType (ANYCAST or MULTICAST)
                queueName,                     // name
                filterExpression,              // filter (null = no filter)
                isDurable,                     // durable
                -1,                            // maxConsumers (-1 = unlimited)
                false,                         // purgeOnNoConsumers
                false,                         // autoCreateAddress
            });

            using var request = CreateManagementRequest("createQueue", body);

            await SendManagementRequestAsync(sender, receiver, replyAddress, request);
        }
#pragma warning disable CA1031
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("ActiveMQQueueExistsException", StringComparison.OrdinalIgnoreCase))
#pragma warning restore CA1031
        {
            LogResourceAlreadyExists(this.logger, queueName, null);
        }
    }

    /// <summary>
    /// Opens a reusable management sender/receiver link pair. Call <see cref="CloseManagementLinksAsync"/>
    /// when all operations are complete. Reusing links avoids the AMQP attach/detach handshake
    /// per request, which dominates topology creation time.
    /// </summary>
    private static async Task<(SenderLink Sender, ReceiverLink Receiver, string ReplyAddress)>
        OpenManagementLinksAsync(Session session)
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

        var replyAddress = await replyAddressTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var senderLink = new SenderLink(session, $"mgmt-send-{Guid.NewGuid():N}", "activemq.management");

        return (senderLink, receiverLink, replyAddress);
    }

    private static async Task CloseManagementLinksAsync(SenderLink sender, ReceiverLink receiver)
    {
        try
        {
            if (!sender.IsClosed)
            {
                await sender.CloseAsync();
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
            if (!receiver.IsClosed)
            {
                await receiver.CloseAsync();
            }
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Sends a management request using pre-opened links.
    /// </summary>
    private static async Task SendManagementRequestAsync(
        SenderLink sender, ReceiverLink receiver, string replyAddress, Message request)
    {
        request.Properties.ReplyTo = replyAddress;
        request.Properties.MessageId = $"mgmt-{Guid.NewGuid():N}";

        await sender.SendAsync(request);

        var reply = await receiver.ReceiveAsync(TimeSpan.FromSeconds(10));
        if (reply is null)
        {
            throw new TimeoutException(
                $"ActiveMQ management request timed out after 10 seconds. Operation: {request.ApplicationProperties?["_AMQ_OperationType"]}");
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
