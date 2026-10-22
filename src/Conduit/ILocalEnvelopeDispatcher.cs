using Conduit.Runtime;

namespace Conduit;

/// <summary>
/// Delivers a durably-stored <see cref="DispatchEnvelope"/> to LOCAL in-process listeners, and
/// classifies message types for the outbox's flush/relay delivery decisions. Implemented by the
/// generated mediator (compile-time type knowledge — no runtime reflection); resolved by
/// Conduit.Durability via <c>GetService</c> so hosts without the source generator degrade to
/// transport-only outbox delivery.
/// </summary>
public interface ILocalEnvelopeDispatcher
{
    /// <summary>
    /// True when <paramref name="messageType"/> is a fire-and-forget event whose local listeners
    /// are delivered from the outbox (captured, non-Immediate). False for plain events — their
    /// listeners already ran inline at publish time, and re-running them on flush/relay would
    /// double-deliver — and for commands, which have no local listener chain.
    /// </summary>
    /// <param name="messageType">The envelope's <see cref="DispatchEnvelope.Type"/> (metadata name).</param>
    public bool DeliversLocally(string messageType);

    /// <summary>
    /// True when <paramref name="messageType"/> is a <c>[LocalOnly]</c> event: flush/relay must
    /// skip the transport send for its outbox entries, exactly as publish-time broadcast skips it.
    /// </summary>
    /// <param name="messageType">The envelope's <see cref="DispatchEnvelope.Type"/> (metadata name).</param>
    public bool IsLocalOnly(string messageType);

    /// <summary>
    /// Deserializes the envelope via the generated type registry and runs the local listener chain
    /// (with pipeline behaviors) against <paramref name="scopedServices"/> — callers supply a fresh
    /// scope per delivery, mirroring fire-and-forget's fresh-scope semantics.
    /// </summary>
    /// <remarks>
    /// The delivery is an observed dispatch: it runs in a child of the caller's current context (the outbox entry's,
    /// restored by the caller) with a fresh <c>MessageId</c> caused by the entry and the entry's <c>MessageId</c> as its
    /// <see cref="MessageContext.DeliveredMessageId"/>, emits
    /// <c>DispatchInvoked</c>/<c>DispatchCompleted</c>/<c>DispatchFailed</c> (kind <c>Event</c>) so each attempt is one
    /// audit record, and restores the caller's context afterwards. It keeps no body: the publish record holds it.
    /// </remarks>
    /// <param name="envelope">The stored envelope (type discriminator + JSON payload).</param>
    /// <param name="scopedServices">The scope to resolve listeners and behaviors from.</param>
    /// <param name="cancellationToken">Cancels listener execution.</param>
    /// <exception cref="InvalidOperationException">The envelope type is not a known event.</exception>
    public Task PublishLocalEnvelopeAsync(DispatchEnvelope envelope, IServiceProvider scopedServices, CancellationToken cancellationToken);
}
