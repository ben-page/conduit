using System.ComponentModel;
using Conduit.Configuration;

namespace Conduit.Runtime;

/// <summary>
/// Sends dispatch envelopes to transports via route resolution. Routed types
/// go to the endpoint's keyed transport; unrouted types broadcast to all
/// registered transports. Registered as a singleton in DI by <c>AddConduitCore</c>. Infrastructure for Conduit's own
/// packages (the outbox's relay and flush); not intended for direct use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
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
    /// Sends a dispatch envelope to the appropriate transport(s). An event
    /// (<see cref="ILocalEnvelopeDispatcher.IsEvent"/>) never routes: it is broadcast with
    /// <see cref="IConduitTransport.SendEventAsync"/> to every registered transport, as the generated publish
    /// broadcasts it, or to none when it is <c>[LocalOnly]</c>. A routed command or query goes to its endpoint's
    /// transport, as a request unless <paramref name="localDispatcher"/> classifies it as <c>[FireAndForget]</c>
    /// (<see cref="ILocalEnvelopeDispatcher.IsFireAndForget"/>), which awaits no reply; an unrouted one to every
    /// transport.
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

        // Events never route (only commands and queries do): a route whose namespace happens to cover an event would
        // otherwise send it point-to-point, as a request awaiting a reply, instead of to every transport
        if (localDispatcher is not null && localDispatcher.IsEvent(envelope.Type))
        {
            if (localDispatcher.IsLocalOnly(envelope.Type))
            {
                return;
            }

            await this.BroadcastAsync(envelope, cancellationToken);
            return;
        }

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
            await this.BroadcastAsync(envelope, cancellationToken);
        }
    }

    // Every non-keyed IConduitTransport: the set the generated publish broadcasts to. Each is a singleton (a transport
    // package registers one forwarder per endpoint), so the set resolved at construction is the one a scope sees.
    private async Task BroadcastAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var transport in this.transports)
        {
            await transport.SendEventAsync(envelope, cancellationToken);
        }
    }
}
