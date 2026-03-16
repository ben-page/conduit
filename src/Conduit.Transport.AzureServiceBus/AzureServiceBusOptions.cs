namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Configuration for the Azure Service Bus transport.
/// </summary>
public sealed class AzureServiceBusOptions
{
    /// <summary>
    /// The Azure Service Bus connection string.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Whether to create topology (topics, queues, subscriptions) on startup.
    /// Default: <see langword="true"/>. Set to <see langword="false"/> for environments
    /// where infrastructure is managed externally.
    /// </summary>
    public bool AutoCreateTopology { get; set; } = true;

    /// <summary>
    /// Maximum concurrent calls for the default receiver processor.
    /// </summary>
    public int DefaultConcurrency { get; set; } = 10;

    /// <summary>
    /// The shared topic name for event pub/sub. Default: "conduit".
    /// </summary>
    public string TopicName { get; set; } = "conduit";

    /// <summary>
    /// Optional prefix for queue names. When set, queues are named
    /// <c>{QueuePrefix}-{ApplicationName}</c> instead of <c>conduit-{ApplicationName}</c>.
    /// </summary>
    public string? QueuePrefix { get; set; }

    /// <summary>
    /// Named receiver pools for concurrency isolation.
    /// Each pool gets its own queue and <c>ServiceBusProcessor</c>.
    /// </summary>
    public List<ReceiverPoolOptions> Pools { get; } = [];

    /// <summary>
    /// Adds a named receiver pool with custom concurrency and message type routing.
    /// </summary>
    /// <param name="name">The pool name (used in queue naming).</param>
    /// <param name="configure">Callback to configure the pool.</param>
    public void AddPool(string name, Action<ReceiverPoolOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var pool = new ReceiverPoolOptions { Name = name };
        configure(pool);
        this.Pools.Add(pool);
    }
}

/// <summary>
/// Configuration for a named receiver pool with dedicated concurrency.
/// </summary>
public sealed class ReceiverPoolOptions
{
    /// <summary>
    /// The pool name, used in queue naming: <c>conduit-{ApplicationName}-{Name}</c>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Maximum concurrent calls for this pool's processor.
    /// </summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>
    /// FullTypeName values of message types routed to this pool.
    /// </summary>
    public List<string> MessageTypes { get; } = [];

    /// <summary>
    /// Adds a message type to this pool by FullTypeName.
    /// </summary>
    /// <param name="messageTypeFullName">The full type name of the message.</param>
    public void Handle(string messageTypeFullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageTypeFullName);
        this.MessageTypes.Add(messageTypeFullName);
    }
}
