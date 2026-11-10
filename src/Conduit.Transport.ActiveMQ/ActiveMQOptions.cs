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
    /// <summary>Creates options with every setting at its default.</summary>
    public ActiveMQOptions()
    {
    }

    /// <summary>Creates options for the endpoint <c>UseActiveMQ()</c> is configuring.</summary>
    internal ActiveMQOptions(string endpointName) => this.EndpointName = endpointName;

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
}
