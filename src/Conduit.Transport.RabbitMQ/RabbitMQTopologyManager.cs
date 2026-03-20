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
    /// Declares all topology: topic exchange, per-app queue with bindings
    /// for subscribed event types and handled command/query types, and pool queues
    /// with their own bindings.
    /// </summary>
    public async Task EnsureTopologyAsync(
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

        // Per-app queue
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

        // Bind app queue to topic exchange for each handled command/query type (excluding pool-assigned)
        foreach (var commandQueryType in handledCommandQueryTypes)
        {
            if (poolAssignedTypes.Contains(commandQueryType))
            {
                continue;
            }

            await channel.QueueBindAsync(
                queue: appQueueName,
                exchange: exchangeName,
                routingKey: commandQueryType,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);
        }

        // Pool queues
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = TopologyNaming.CreateAppResourceName(
                this.conduitOptions, pool.Name, 255);
            await channel.QueueDeclareAsync(
                queue: poolQueueName,
                durable: !isEphemeral,
                exclusive: false,
                autoDelete: isEphemeral,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);

            foreach (var messageType in pool.MessageTypes)
            {
                await channel.QueueBindAsync(
                    queue: poolQueueName,
                    exchange: exchangeName,
                    routingKey: messageType,
                    arguments: null,
                    noWait: false,
                    cancellationToken: cancellationToken);
            }
        }
    }
}
