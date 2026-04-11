namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// Configuration for the ActiveMQ Artemis transport. Passed to <c>UseActiveMQ()</c>
/// on the endpoint builder.
/// </summary>
public sealed class ActiveMQOptions
{
    /// <summary>
    /// AMQP 1.0 connection string (e.g. "amqp://localhost:5672").
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Optional username for AMQP SASL PLAIN authentication.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Optional password for AMQP SASL PLAIN authentication.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default prefetch count (AMQP link credit) for receiver links. Controls concurrency per consumer.
    /// </summary>
    public int DefaultPrefetchCount { get; set; } = 10;

    /// <summary>
    /// The endpoint name this transport is registered under in keyed DI.
    /// Set automatically by <c>UseActiveMQ()</c> from the <c>EndpointBuilder</c> context.
    /// Used by dead letter queue capture to record the source endpoint and by replay to
    /// resolve the correct transport.
    /// </summary>
    public string EndpointName { get; internal set; } = string.Empty;

    /// <summary>
    /// Named receiver pools with separate receiver links and prefetch settings.
    /// </summary>
    public List<ActiveMQReceiverPoolOptions> Pools { get; } = [];

    /// <summary>
    /// Adds a named receiver pool with the specified configuration.
    /// </summary>
    public void AddPool(string name, Action<ActiveMQReceiverPoolOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var pool = new ActiveMQReceiverPoolOptions { Name = name };
        configure(pool);
        this.Pools.Add(pool);
    }
}
