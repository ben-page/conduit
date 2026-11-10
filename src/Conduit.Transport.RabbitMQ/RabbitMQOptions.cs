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
    /// <summary>Creates options with every setting at its default.</summary>
    public RabbitMQOptions()
    {
    }

    /// <summary>Creates options for the endpoint <c>UseRabbitMQ()</c> is configuring.</summary>
    internal RabbitMQOptions(string endpointName) => this.EndpointName = endpointName;

    /// <summary>
    /// AMQP connection string (e.g. "amqp://guest:guest@localhost:5672/").
    /// Supplied in code or from the endpoint's <c>ConnectionString</c> configuration key;
    /// validated after both are applied.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
