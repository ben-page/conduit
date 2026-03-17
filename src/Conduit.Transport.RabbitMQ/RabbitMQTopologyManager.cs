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
    private readonly string applicationName;

    public RabbitMQTopologyManager(RabbitMQOptions options, string applicationName)
    {
        this.options = options;
        this.applicationName = applicationName;
    }

    /// <summary>
    /// Declares all topology: topic exchange for events, per-app queue with bindings
    /// for subscribed event types, and pool queues with their own bindings.
    /// </summary>
    public async Task EnsureTopologyAsync(
        IChannel channel,
        IReadOnlyList<string> subscribedEventTypes,
        CancellationToken cancellationToken)
    {
        // Topic exchange for events
        await channel.ExchangeDeclareAsync(
            exchange: this.options.ExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        // Direct exchange for command/query routing
        await channel.ExchangeDeclareAsync(
            exchange: $"{this.options.ExchangeName}.direct",
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        // Per-app queue for events
        var appQueueName = this.GetAppQueueName();
        await channel.QueueDeclareAsync(
            queue: appQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        // Bind app queue to direct exchange so it can receive commands/queries
        await channel.QueueBindAsync(
            queue: appQueueName,
            exchange: $"{this.options.ExchangeName}.direct",
            routingKey: appQueueName,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        // Bind app queue to topic exchange for each subscribed event type
        foreach (var eventType in subscribedEventTypes)
        {
            await channel.QueueBindAsync(
                queue: appQueueName,
                exchange: this.options.ExchangeName,
                routingKey: eventType,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);
        }

        // Pool queues
        foreach (var pool in this.options.Pools)
        {
            var poolQueueName = $"{appQueueName}-{pool.Name}";
            await channel.QueueDeclareAsync(
                queue: poolQueueName,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                noWait: false,
                cancellationToken: cancellationToken);

            foreach (var messageType in pool.MessageTypes)
            {
                await channel.QueueBindAsync(
                    queue: poolQueueName,
                    exchange: this.options.ExchangeName,
                    routingKey: messageType,
                    arguments: null,
                    noWait: false,
                    cancellationToken: cancellationToken);
            }
        }
    }

    /// <summary>
    /// Declares a command queue for a specific endpoint and binds it to the direct exchange.
    /// Called on first use when routing a command to a named endpoint.
    /// </summary>
    public async Task EnsureCommandQueueAsync(
        IChannel channel,
        string endpointName,
        CancellationToken cancellationToken)
    {
        var queueName = this.GetCommandQueueName(endpointName);
        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);

        await channel.QueueBindAsync(
            queue: queueName,
            exchange: $"{this.options.ExchangeName}.direct",
            routingKey: queueName,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets the application's main queue name for event consumption.
    /// </summary>
    public string GetAppQueueName() =>
        this.options.QueuePrefix is not null
            ? $"{this.options.QueuePrefix}-{this.applicationName}"
            : $"conduit-{this.applicationName}";

    /// <summary>
    /// Gets the command queue name for a given endpoint.
    /// </summary>
    public string GetCommandQueueName(string endpointName) =>
        this.options.QueuePrefix is not null
            ? $"{this.options.QueuePrefix}-{endpointName}"
            : $"conduit-{endpointName}";
}
