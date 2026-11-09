using Conduit.Runtime;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Configuration for the RabbitMQ transport. Passed to <c>UseRabbitMQ()</c>
/// on the endpoint builder.
/// </summary>
/// <remarks>
/// <see cref="BrokerTransportOptions.DefaultConcurrency"/> and
/// <see cref="ReceiverPoolOptions.Concurrency"/> map to the consumer <c>BasicQos</c> prefetch count.
/// </remarks>
public sealed class RabbitMQOptions : BrokerTransportOptions
{
    /// <summary>
    /// AMQP connection string (e.g. "amqp://guest:guest@localhost:5672/").
    /// Supplied in code or from the endpoint's <c>ConnectionString</c> configuration key;
    /// validated after both are applied.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The endpoint name this transport is registered under in keyed DI.
    /// Set automatically by <c>UseRabbitMQ()</c> from the <c>ConduitEndpointBuilder</c> context.
    /// Used by dead letter queue capture to record the source endpoint and by replay to
    /// resolve the correct transport.
    /// </summary>
    public string EndpointName { get; internal set; } = string.Empty;
}
