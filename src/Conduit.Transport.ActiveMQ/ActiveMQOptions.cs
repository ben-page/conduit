using Conduit.Runtime;

namespace Conduit.Transport.ActiveMQ;

/// <summary>
/// Configuration for the ActiveMQ Artemis transport. Passed to <c>UseActiveMQ()</c>
/// on the endpoint builder.
/// </summary>
/// <remarks>
/// <see cref="BrokerTransportOptions.DefaultConcurrency"/> and
/// <see cref="ReceiverPoolOptions.Concurrency"/> map to the receiver link prefetch count
/// (AMQP link credit).
/// </remarks>
public sealed class ActiveMQOptions : BrokerTransportOptions
{
    /// <summary>
    /// AMQP 1.0 connection string (e.g. "amqp://localhost:5672").
    /// Supplied in code or from the endpoint's <c>ConnectionString</c> configuration key;
    /// validated after both are applied.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

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
    /// The endpoint name this transport is registered under in keyed DI.
    /// Set automatically by <c>UseActiveMQ()</c> from the <c>ConduitEndpointBuilder</c> context.
    /// Used by dead letter queue capture to record the source endpoint and by replay to
    /// resolve the correct transport.
    /// </summary>
    public string EndpointName { get; internal set; } = string.Empty;
}
