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
    // The shortest time to live Service Bus accepts, for a message as for a queue's default
    private static readonly TimeSpan MinimumTimeToLive = TimeSpan.FromSeconds(1);

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

    /// <summary>
    /// How long a reply waits in a reply queue to be read before Service Bus discards it, both the reply's own
    /// time to live and the reply queue's default: twice <see cref="BrokerTransportOptions.ResponseTimeout"/>,
    /// and never less than one second, the shortest Service Bus accepts. A request reads its reply as soon as it
    /// arrives or never (the request timed out, or its process is gone), and nothing else reads a request's
    /// session. Null, for no expiry at all, when the response timeout has no finite double: infinite
    /// (<see cref="Timeout.InfiniteTimeSpan"/>), not positive, or longer than half of <see cref="TimeSpan.MaxValue"/>.
    /// </summary>
    internal TimeSpan? ReplyTimeToLive
    {
        get
        {
            var responseTimeoutTicks = this.ResponseTimeout.Ticks;
            if (responseTimeoutTicks is <= 0 or > long.MaxValue / 2)
            {
                return null;
            }

            var twice = TimeSpan.FromTicks(responseTimeoutTicks * 2);
            return twice < MinimumTimeToLive ? MinimumTimeToLive : twice;
        }
    }
}
