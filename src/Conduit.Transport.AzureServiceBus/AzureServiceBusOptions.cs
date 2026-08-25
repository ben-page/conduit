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

    /// <summary>
    /// Timeout for request/response operations. Default 60 seconds.
    /// </summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The endpoint name this transport is registered under in keyed DI.
    /// Set automatically by <c>UseAzureServiceBus()</c> from the <c>EndpointBuilder</c> context.
    /// Used by dead letter queue capture to record the source endpoint and by replay to
    /// resolve the correct transport.
    /// </summary>
    public string EndpointName { get; internal set; } = string.Empty;
}
