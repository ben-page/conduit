using Conduit.Runtime;

namespace Conduit.Transport.AzureServiceBus;

/// <summary>
/// Configuration for the Azure Service Bus transport.
/// </summary>
/// <remarks>
/// <see cref="BrokerTransportOptions.DefaultConcurrency"/> and
/// <see cref="ReceiverPoolOptions.Concurrency"/> map to the processor's max-concurrent-calls.
/// </remarks>
public sealed class AzureServiceBusOptions : BrokerTransportOptions
{
    /// <summary>Creates options with every setting at its default.</summary>
    public AzureServiceBusOptions()
    {
    }

    /// <summary>Creates options for the endpoint <c>UseAzureServiceBus()</c> is configuring.</summary>
    internal AzureServiceBusOptions(string endpointName) => this.EndpointName = endpointName;

    /// <summary>
    /// The Azure Service Bus connection string.
    /// Supplied in code or from the endpoint's <c>ConnectionString</c> configuration key;
    /// validated after both are applied.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Connection string for the administration client (topology management).
    /// Defaults to <see cref="ConnectionString"/> if not set. Required when using the
    /// Azure Service Bus Emulator, which exposes AMQP and HTTP on different ports.
    /// </summary>
    public string? AdminConnectionString { get; set; }
}
