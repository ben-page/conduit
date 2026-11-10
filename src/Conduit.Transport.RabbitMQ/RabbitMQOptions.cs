using Conduit.Runtime;

namespace Conduit.Transport.RabbitMQ;

/// <summary>
/// Configuration for the RabbitMQ transport. Passed to <c>UseRabbitMQ()</c>
/// on the endpoint builder.
/// </summary>
/// <remarks>
/// <see cref="BrokerTransportOptions.DefaultConcurrency"/> and
/// <see cref="ReceiverPoolOptions.Concurrency"/> are each a consumer's concurrency: its channel's
/// <c>BasicQos</c> prefetch count and its consumer dispatch concurrency, so up to that many of its
/// handlers run at once. Both are AMQP shorts: <c>UseRabbitMQ()</c> refuses a value outside 1..65535.
/// </remarks>
public sealed class RabbitMQOptions : BrokerTransportOptions
{
    /// <summary>The largest concurrency a consumer accepts: an AMQP short.</summary>
    internal const int MaxConcurrency = ushort.MaxValue;

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

    /// <summary>
    /// The instance id in the name of the transport's first reply queue
    /// (<c>{system}-{app}-replies-{instanceId}</c>), so a test can take that name before the host starts.
    /// Null, outside tests: a fresh id. Internal, so configuration never binds it.
    /// </summary>
    internal string? InitialReplyQueueInstanceId { get; set; }

    /// <summary>
    /// Throws when <see cref="BrokerTransportOptions.DefaultConcurrency"/> or a pool's
    /// <see cref="ReceiverPoolOptions.Concurrency"/> is outside 1..65535, which a consumer's prefetch
    /// count and dispatch concurrency (both <see cref="ushort"/>) would otherwise silently wrap.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A concurrency is outside 1..65535.</exception>
    internal void ThrowIfConcurrencyOutOfRange()
    {
        if (this.DefaultConcurrency is < 1 or > MaxConcurrency)
        {
            throw new ArgumentOutOfRangeException(
                nameof(this.DefaultConcurrency),
                this.DefaultConcurrency,
                $"RabbitMQ endpoint '{this.EndpointName}': DefaultConcurrency must be between 1 and {MaxConcurrency}.");
        }

        foreach (var pool in this.Pools)
        {
            if (pool.Concurrency is < 1 or > MaxConcurrency)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ReceiverPoolOptions.Concurrency),
                    pool.Concurrency,
                    $"RabbitMQ endpoint '{this.EndpointName}': the Concurrency of pool '{pool.Name}' must be between 1 and {MaxConcurrency}.");
            }
        }
    }
}
