using Conduit.Configuration;
using Conduit.Runtime;
using RabbitMQ.Client;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Declares RabbitMQ exchanges, queues, and bindings. All declarations are idempotent —
/// if the entity already exists with the same configuration, the call is a no-op.
/// If it exists with a different configuration, RabbitMQ throws (topology mismatch is a real error).
/// </summary>
internal sealed class RabbitMQTopologyManager
{
    /// <summary>RabbitMQ's limit on exchange and queue names.</summary>
    private const int MaxNameLength = 255;

    /// <summary>How long the dead-letter queue of a Test-mode system lives once nothing uses it.</summary>
    private static readonly TimeSpan TestModeDeadLetterExpiry = TimeSpan.FromMinutes(5);

    private readonly RabbitMQOptions options;
    private readonly ConduitOptions conduitOptions;

    public RabbitMQTopologyManager(RabbitMQOptions options, ConduitOptions conduitOptions)
    {
        this.options = options;
        this.conduitOptions = conduitOptions;
    }

    /// <summary>
    /// The name of the system's dead-letter exchange and of the queue bound to it, <c>{system}-dead-letter</c>:
    /// one of each per system, shared by every application in it.
    /// </summary>
    public static string DeadLetterName(ConduitOptions conduitOptions) =>
        TopologyNaming.CreateSharedResourceName(conduitOptions, "dead-letter", MaxNameLength);

    /// <summary>
    /// The name of one transport instance's reply queue, <c>{system}-{app}-replies-{instanceId}</c>. The
    /// client names it, not the broker, so the queue automatic recovery re-declares after a lost connection
    /// keeps the name every request carries in <c>ReplyTo</c>.
    /// </summary>
    public static string ReplyQueueName(ConduitOptions conduitOptions, string instanceId) =>
        TopologyNaming.CreateAppResourceName(conduitOptions, $"replies-{instanceId}", MaxNameLength);

    /// <summary>
    /// Declares only the topic exchange. Used by ProducerOnly mode to enable publishing
    /// without creating inbound queues or bindings.
    /// </summary>
    public async Task EnsureExchangeAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var isEphemeral = this.conduitOptions.TopologyMode != TopologyMode.Production;

        var exchangeName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, MaxNameLength);
        await channel.ExchangeDeclareAsync(
            exchange: exchangeName,
            type: ExchangeType.Topic,
            durable: !isEphemeral,
            autoDelete: isEphemeral,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Declares all topology: topic exchange, the system's dead-letter exchange and queue, per-app queue
    /// with event bindings (multicast), shared queues with command/query bindings (unicast), and pool
    /// queues with their own bindings. Every inbound queue dead-letters what its consumer rejects.
    /// Returns the topology result with queue names and their associated concurrency.
    /// </summary>
    public async Task<TopologyResult> EnsureTopologyAsync(
        IChannel channel,
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        var isEphemeral = this.conduitOptions.TopologyMode != TopologyMode.Production;

        // Topic exchange for all message types
        var exchangeName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, MaxNameLength);
        await channel.ExchangeDeclareAsync(
            exchange: exchangeName,
            type: ExchangeType.Topic,
            durable: !isEphemeral,
            autoDelete: isEphemeral,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        var deadLetterExchangeName = await this.EnsureDeadLetterAsync(channel, isEphemeral, cancellationToken);

        // Per-app queue for events (multicast — each app gets its own copy)
        var appQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, MaxNameLength);
        await DeclareInboundQueueAsync(channel, appQueueName, isEphemeral, deadLetterExchangeName, cancellationToken);

        // Collect pool-assigned types so they are only bound to their pool queue, not the default queue
        var poolAssignedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pool in this.options.Pools)
        {
            foreach (var messageType in pool.MessageTypes)
            {
                poolAssignedTypes.Add(messageType);
            }
        }

        // Bind app queue to topic exchange for each subscribed event type (excluding pool-assigned)
        foreach (var eventType in subscribedEventTypes)
        {
            if (poolAssignedTypes.Contains(eventType))
            {
                continue;
            }

            await channel.QueueBindAsync(
                queue: appQueueName,
                exchange: exchangeName,
                routingKey: eventType,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);
        }

        // Shared queues for commands/queries (unicast — competing consumers across apps)
        var sharedCommandQueryQueues = new List<string>();

        foreach (var commandQueryType in handledCommandQueryTypes)
        {
            if (poolAssignedTypes.Contains(commandQueryType))
            {
                continue;
            }

            var sharedQueueName = TopologyNaming.CreateSharedResourceName(
                this.conduitOptions, commandQueryType, MaxNameLength);

            await DeclareInboundQueueAsync(channel, sharedQueueName, isEphemeral, deadLetterExchangeName, cancellationToken);

            await channel.QueueBindAsync(
                queue: sharedQueueName,
                exchange: exchangeName,
                routingKey: commandQueryType,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);

            sharedCommandQueryQueues.Add(sharedQueueName);
        }

        // Pool queues — events use per-app pool queues (multicast),
        // commands/queries use shared pool queues (unicast)
        var commandQuerySet = new HashSet<string>(handledCommandQueryTypes, StringComparer.Ordinal);
        var poolEventQueues = new List<(string QueueName, ushort Concurrency)>();
        var poolCommandQueryQueues = new List<(string QueueName, ushort Concurrency)>();

        foreach (var pool in this.options.Pools)
        {
            // UseRabbitMQ refused a concurrency outside 1..65535, so the cast never wraps
            var poolConcurrency = (ushort)pool.Concurrency;
            var hasPoolEvents = false;
            var poolEventQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, MaxNameLength);

            foreach (var messageType in pool.MessageTypes)
            {
                if (commandQuerySet.Contains(messageType))
                {
                    // Command/query in pool → shared pool queue (unicast — competing consumers)
                    var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
                        this.conduitOptions, $"{pool.Name}.{messageType}", MaxNameLength);

                    await DeclareInboundQueueAsync(
                        channel, sharedPoolQueueName, isEphemeral, deadLetterExchangeName, cancellationToken);

                    await channel.QueueBindAsync(
                        queue: sharedPoolQueueName,
                        exchange: exchangeName,
                        routingKey: messageType,
                        arguments: null,
                        noWait: false,
                        cancellationToken: cancellationToken);

                    poolCommandQueryQueues.Add((sharedPoolQueueName, poolConcurrency));
                }
                else
                {
                    // Event in pool → per-app pool queue (multicast)
                    if (!hasPoolEvents)
                    {
                        await DeclareInboundQueueAsync(
                            channel, poolEventQueueName, isEphemeral, deadLetterExchangeName, cancellationToken);
                        hasPoolEvents = true;
                    }

                    await channel.QueueBindAsync(
                        queue: poolEventQueueName,
                        exchange: exchangeName,
                        routingKey: messageType,
                        arguments: null,
                        noWait: false,
                        cancellationToken: cancellationToken);
                }
            }

            if (hasPoolEvents)
            {
                poolEventQueues.Add((poolEventQueueName, poolConcurrency));
            }
        }

        return new TopologyResult(sharedCommandQueryQueues, poolEventQueues, poolCommandQueryQueues);
    }

    /// <summary>
    /// Declares the system's dead-letter exchange (fanout) and the queue bound to it, both
    /// <c>{system}-dead-letter</c>, and returns the exchange's name. What a consumer rejects without
    /// requeue (<c>BasicNack</c> with <c>requeue: false</c>) is routed there with its properties intact.
    /// The queue itself has no dead-letter exchange: nothing consumes it here.
    /// </summary>
    /// <remarks>
    /// The queue is never auto-delete, in any mode: nothing here consumes it, so auto-delete would never
    /// fire, except when someone's consumer attached and left again, taking the dead letters with it.
    /// It is durable in Production, and lives until the broker restarts in Developer mode (one per
    /// developer prefix). In Test mode, where every run has a prefix of its own, it expires
    /// (<c>x-expires</c>) once unused for <see cref="TestModeDeadLetterExpiry"/>, and the auto-delete
    /// exchange goes with its last binding.
    /// </remarks>
    private async Task<string> EnsureDeadLetterAsync(
        IChannel channel,
        bool isEphemeral,
        CancellationToken cancellationToken)
    {
        var deadLetterName = DeadLetterName(this.conduitOptions);

        await channel.ExchangeDeclareAsync(
            exchange: deadLetterName,
            type: ExchangeType.Fanout,
            durable: !isEphemeral,
            autoDelete: isEphemeral,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        var queueArguments = this.conduitOptions.TopologyMode == TopologyMode.Test
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-expires"] = (int)TestModeDeadLetterExpiry.TotalMilliseconds,
            }
            : null;

        await channel.QueueDeclareAsync(
            queue: deadLetterName,
            durable: !isEphemeral,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: deadLetterName,
            exchange: deadLetterName,
            routingKey: string.Empty,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        return deadLetterName;
    }

    /// <summary>
    /// Declares a queue a consumer of this transport reads, with <c>x-dead-letter-exchange</c> naming the
    /// system's dead-letter exchange: a message the consumer rejects goes there instead of back to the queue.
    /// </summary>
    private static async Task DeclareInboundQueueAsync(
        IChannel channel,
        string queueName,
        bool isEphemeral,
        string deadLetterExchangeName,
        CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: !isEphemeral,
            exclusive: false,
            autoDelete: isEphemeral,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-dead-letter-exchange"] = deadLetterExchangeName,
            },
            noWait: false,
            cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Result of topology setup containing all queue names that need consumers.
/// </summary>
internal sealed class TopologyResult
{
    /// <summary>
    /// Shared command/query queue names (unicast, default concurrency).
    /// </summary>
    public IReadOnlyList<string> SharedCommandQueryQueues { get; }

    /// <summary>
    /// Pool event queue names with their pool-specific concurrency (multicast, per-app).
    /// </summary>
    public IReadOnlyList<(string QueueName, ushort Concurrency)> PoolEventQueues { get; }

    /// <summary>
    /// Pool command/query queue names with their pool-specific concurrency (unicast, shared).
    /// </summary>
    public IReadOnlyList<(string QueueName, ushort Concurrency)> PoolCommandQueryQueues { get; }

    public TopologyResult(
        IReadOnlyList<string> sharedCommandQueryQueues,
        IReadOnlyList<(string QueueName, ushort Concurrency)> poolEventQueues,
        IReadOnlyList<(string QueueName, ushort Concurrency)> poolCommandQueryQueues)
    {
        this.SharedCommandQueryQueues = sharedCommandQueryQueues;
        this.PoolEventQueues = poolEventQueues;
        this.PoolCommandQueryQueues = poolCommandQueryQueues;
    }
}
