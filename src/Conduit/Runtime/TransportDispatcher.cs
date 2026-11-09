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
    /// Sends a dispatch envelope to the appropriate transport(s): a routed type to its endpoint's transport, as a
    /// request unless <paramref name="localDispatcher"/> classifies it as <c>[FireAndForget]</c>
    /// (<see cref="ILocalEnvelopeDispatcher.IsFireAndForget"/>), which is sent with
    /// <see cref="IConduitTransport.SendEventAsync"/> and awaits no reply; an unrouted type to every transport.
    /// </summary>
    /// <param name="envelope">The envelope to send.</param>
    /// <param name="localDispatcher">
    /// The generated message classifier, resolved by the caller from its own scope, or null when the host has none.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    public async Task SendAsync(
        DispatchEnvelope envelope,
        ILocalEnvelopeDispatcher? localDispatcher,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var endpoint = ConduitRuntime.ResolveRoute(this.options, envelope.Type);
        if (endpoint != null)
        {
            var transport = ConduitRuntime.ResolveTransport(this.serviceProvider, endpoint);

            // A [FireAndForget] message expects no reply: awaiting one would hold the relay until the response
            // timeout and then count a delivered message as failed
            if (localDispatcher is not null && localDispatcher.IsFireAndForget(envelope.Type))
            {
                await transport.SendEventAsync(envelope, cancellationToken);
            }
            else
            {
                await transport.SendRequestAsync(envelope, cancellationToken);
            }
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
