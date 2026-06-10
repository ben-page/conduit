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
    private readonly RabbitMQOptions options;
    private readonly ConduitOptions conduitOptions;

    public RabbitMQTopologyManager(RabbitMQOptions options, ConduitOptions conduitOptions)
    {
        this.options = options;
        this.conduitOptions = conduitOptions;
    }

    /// <summary>
    /// Declares only the topic exchange. Used by ProducerOnly mode to enable publishing
    /// without creating inbound queues or bindings.
    /// </summary>
    public async Task EnsureExchangeAsync(IChannel channel, CancellationToken cancellationToken)
    {
        var isEphemeral = this.conduitOptions.TopologyMode != TopologyMode.Production;

        var exchangeName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 255);
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
    /// Declares all topology: topic exchange, per-app queue with event bindings (multicast),
    /// shared queues with command/query bindings (unicast), and pool queues with their own bindings.
    /// Returns the topology result with queue names and their associated prefetch counts.
    /// </summary>
    public async Task<TopologyResult> EnsureTopologyAsync(
        IChannel channel,
        IReadOnlyList<string> subscribedEventTypes,
        IReadOnlyList<string> handledCommandQueryTypes,
        CancellationToken cancellationToken)
    {
        var isEphemeral = this.conduitOptions.TopologyMode != TopologyMode.Production;

        // Topic exchange for all message types
        var exchangeName = TopologyNaming.CreateSharedResourceName(this.conduitOptions, 255);
        await channel.ExchangeDeclareAsync(
            exchange: exchangeName,
            type: ExchangeType.Topic,
            durable: !isEphemeral,
            autoDelete: isEphemeral,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        // Per-app queue for events (multicast — each app gets its own copy)
        var appQueueName = TopologyNaming.CreateAppResourceName(this.conduitOptions, 255);
        await channel.QueueDeclareAsync(
            queue: appQueueName,
            durable: !isEphemeral,
            exclusive: false,
            autoDelete: isEphemeral,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

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
                this.conduitOptions, commandQueryType, 255);

            await channel.QueueDeclareAsync(
                queue: sharedQueueName,
                durable: !isEphemeral,
                exclusive: false,
                autoDelete: isEphemeral,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);

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
        var poolEventQueues = new List<(string QueueName, ushort PrefetchCount)>();
        var poolCommandQueryQueues = new List<(string QueueName, ushort PrefetchCount)>();

        foreach (var pool in this.options.Pools)
        {
            var hasPoolEvents = false;
            var poolEventQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 255);

            foreach (var messageType in pool.MessageTypes)
            {
                if (commandQuerySet.Contains(messageType))
                {
                    // Command/query in pool → shared pool queue (unicast — competing consumers)
                    var sharedPoolQueueName = TopologyNaming.CreateSharedResourceName(
                        this.conduitOptions, $"{pool.Name}.{messageType}", 255);

                    await channel.QueueDeclareAsync(
                        queue: sharedPoolQueueName,
                        durable: !isEphemeral,
                        exclusive: false,
                        autoDelete: isEphemeral,
                        arguments: null,
                        noWait: false,
                        cancellationToken: cancellationToken);

                    await channel.QueueBindAsync(
                        queue: sharedPoolQueueName,
                        exchange: exchangeName,
                        routingKey: messageType,
                        arguments: null,
                        noWait: false,
                        cancellationToken: cancellationToken);

                    poolCommandQueryQueues.Add((sharedPoolQueueName, (ushort)pool.Concurrency));
                }
                else
                {
                    // Event in pool → per-app pool queue (multicast)
                    if (!hasPoolEvents)
                    {
                        await channel.QueueDeclareAsync(
                            queue: poolEventQueueName,
                            durable: !isEphemeral,
                            exclusive: false,
                            autoDelete: isEphemeral,
                            arguments: null,
                            noWait: false,
                            cancellationToken: cancellationToken);
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
                poolEventQueues.Add((poolEventQueueName, (ushort)pool.Concurrency));
            }
        }

        return new TopologyResult(sharedCommandQueryQueues, poolEventQueues, poolCommandQueryQueues);
    }
}

/// <summary>
/// Result of topology setup containing all queue names that need consumers.
/// </summary>
internal sealed class TopologyResult
{
    /// <summary>
    /// Shared command/query queue names (unicast, default prefetch count).
    /// </summary>
    public IReadOnlyList<string> SharedCommandQueryQueues { get; }

    /// <summary>
    /// Pool event queue names with their pool-specific prefetch counts (multicast, per-app).
    /// </summary>
    public IReadOnlyList<(string QueueName, ushort PrefetchCount)> PoolEventQueues { get; }

    /// <summary>
    /// Pool command/query queue names with their pool-specific prefetch counts (unicast, shared).
    /// </summary>
    public IReadOnlyList<(string QueueName, ushort PrefetchCount)> PoolCommandQueryQueues { get; }

    public TopologyResult(
        IReadOnlyList<string> sharedCommandQueryQueues,
        IReadOnlyList<(string QueueName, ushort PrefetchCount)> poolEventQueues,
        IReadOnlyList<(string QueueName, ushort PrefetchCount)> poolCommandQueryQueues)
    {
        this.SharedCommandQueryQueues = sharedCommandQueryQueues;
        this.PoolEventQueues = poolEventQueues;
        this.PoolCommandQueryQueues = poolCommandQueryQueues;
    }
}
