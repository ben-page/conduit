namespace Conduit.Runtime;

/// <summary>
/// Base configuration shared by all broker transports (RabbitMQ, Azure Service Bus,
/// Amazon SQS, ActiveMQ). Owns the default consumer concurrency knob and named receiver pools.
/// Concrete transport options classes inherit this and add only broker-specific settings
/// (connection details, region/credentials, producer/consumer config, etc.).
/// </summary>
public abstract class BrokerTransportOptions
{
    /// <summary>
    /// Default concurrency budget for the transport's default consumer. Interpreted per broker
    /// (prefetch count for RabbitMQ/ActiveMQ, max concurrent calls for Azure Service Bus, polling
    /// parallelism for Amazon SQS). Defaults to 10.
    /// </summary>
    public int DefaultConcurrency { get; set; } = 10;

    /// <summary>
    /// How long a request waits for its reply. A request with no reply within it fails with a
    /// <see cref="TimeoutException"/> naming the request, the endpoint and this timeout. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The endpoint name this transport is registered under in keyed DI. Set by the transport's
    /// <c>Use*</c> extension from the <c>ConduitEndpointBuilder</c> context, never from configuration:
    /// the endpoint's identity is not a setting. Dead-letter capture records it as the source endpoint
    /// and replay resolves the transport by it.
    /// </summary>
    public string EndpointName { get; protected set; } = string.Empty;

    /// <summary>
    /// Named receiver pools for concurrency isolation. Each pool routes its assigned message types
    /// onto a dedicated consumer with its own <see cref="ReceiverPoolOptions.Concurrency"/> budget.
    /// </summary>
    public List<ReceiverPoolOptions> Pools { get; } = [];

    /// <summary>
    /// Gets or creates a named receiver pool with custom concurrency and message type routing.
    /// If a pool with this name already exists, configures the existing pool instead of adding a new one.
    /// This allows configuration and code to merge on the same named pool.
    /// </summary>
    /// <param name="name">The pool name (used in broker-specific resource naming). <c>replies</c> and
    /// <c>dead-letter</c> are reserved, in any case and however written before sanitizing.</param>
    /// <param name="configure">Callback to configure the pool.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank or reserved.</exception>
    public void AddPool(string name, Action<ReceiverPoolOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        if (IsReservedPoolName(name))
        {
            throw new ArgumentException(
                $"The receiver pool name '{name}' is reserved: its queue would be named like a transport's own " +
                "reply or dead-letter queue. Choose another name.",
                nameof(name));
        }

        // Get-or-create: a pool of this name may already exist because configuration declared it.
        // Configuration supplies operational values, code supplies message types; both merge here.
        // Matching is case-insensitive, consistent with configuration keys elsewhere.
        var pool = this.Pools.FirstOrDefault(existing => string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase));

        if (pool is null)
        {
            pool = new ReceiverPoolOptions { Name = name };
            this.Pools.Add(pool);
        }

        configure(pool);
    }

    /// <summary>
    /// Whether <paramref name="name"/> would give a pool's queue the name of a transport's own reply or
    /// dead-letter queue: <c>replies</c> or <c>dead-letter</c>, compared as resource names are built
    /// (sanitized, so <c>dead letter</c> is <c>dead-letter</c>) and ignoring case. <see cref="AddPool"/> and
    /// the configuration binder refuse such a name.
    /// </summary>
    internal static bool IsReservedPoolName(string name)
    {
        var resourceName = TopologyNaming.Sanitize(name);
        return string.Equals(resourceName, "replies", StringComparison.OrdinalIgnoreCase)
            || string.Equals(resourceName, "dead-letter", StringComparison.OrdinalIgnoreCase);
    }
}
