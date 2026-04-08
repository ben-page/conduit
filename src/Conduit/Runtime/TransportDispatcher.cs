using Conduit.Configuration;

namespace Conduit.Runtime;

/// <summary>
/// Sends dispatch envelopes to transports via route resolution. Routed types
/// go to the endpoint's keyed transport; unrouted types broadcast to all
/// registered transports. Registered as a singleton in DI by <c>AddConduitCore</c>.
/// </summary>
public sealed class TransportDispatcher
{
    private readonly ConduitOptions options;
    private readonly IServiceProvider serviceProvider;
    private readonly IConduitTransport[] transports;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransportDispatcher"/> class.
    /// </summary>
    public TransportDispatcher(
        ConduitOptions options,
        IServiceProvider serviceProvider,
        IEnumerable<IConduitTransport> transports)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(transports);

        this.options = options;
        this.serviceProvider = serviceProvider;
        this.transports = transports.ToArray();
    }

    /// <summary>
    /// Sends a dispatch envelope to the appropriate transport(s).
    /// </summary>
    public async Task SendAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var endpoint = ConduitRuntime.ResolveRoute(this.options, envelope.Type);
        if (endpoint != null)
        {
            var transport = ConduitRuntime.ResolveTransport(this.serviceProvider, endpoint);
            await transport.SendRequestAsync(envelope, cancellationToken);
        }
        else
        {
            foreach (var transport in this.transports)
            {
                await transport.SendEventAsync(envelope, cancellationToken);
            }
        }
    }
}
