namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Configuration for the RabbitMQ transport. Passed to <c>UseRabbitMQ()</c>
/// on the endpoint builder.
/// </summary>
public sealed class RabbitMQOptions
{
    /// <summary>
    /// AMQP connection string (e.g. "amqp://guest:guest@localhost:5672/").
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default prefetch count for consumer channels. Controls concurrency per consumer.
    /// </summary>
    public ushort DefaultPrefetchCount { get; set; } = 10;

    /// <summary>
    /// Named receiver pools with separate consumer channels and prefetch settings.
    /// </summary>
    public List<ReceiverPoolOptions> Pools { get; } = [];
}

/// <summary>
/// Configuration for a named receiver pool with its own consumer channel and prefetch count.
/// </summary>
public sealed class ReceiverPoolOptions
{
    /// <summary>
    /// Unique name for this pool. Used in queue naming: "{app-queue}-{Name}".
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Prefetch count for this pool's consumer channel.
    /// </summary>
    public ushort PrefetchCount { get; set; } = 1;

    /// <summary>
    /// FullTypeNames of messages routed to this pool's queue.
    /// </summary>
    public List<string> MessageTypes { get; } = [];

    /// <summary>
    /// Adds a message type (by FullTypeName) to this pool.
    /// </summary>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.MessageTypes.Add(messageTypeFullName);
    }
}
