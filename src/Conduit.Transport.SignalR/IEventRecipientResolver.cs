namespace Conduit.Transport.SignalR;

/// <summary>
/// Decides which SignalR connections the server-mode <see cref="SignalRTransport"/> pushes a published event to. Register
/// one in DI (<c>services.AddSingleton&lt;IEventRecipientResolver, MyResolver&gt;()</c>) and it is asked about every event
/// the host publishes that is not <c>[LocalOnly]</c>, <c>[ClientVisible]</c> or not: it replaces the default, which pushes
/// a <c>[ClientVisible]</c> event to every connection and any other event to none.
/// </summary>
/// <remarks>
/// The transport is a singleton and resolves the resolver once, from the root container, so register it as a singleton;
/// one that needs scoped services (a database context, say) creates a scope from <c>IServiceScopeFactory</c> per call.
/// <para>
/// It runs once per event: inline in the publisher's <c>PublishAsync</c>, or, for an event the outbox captured, later on
/// the outbox's delivery (its flush or relay), after the publisher's request has ended. There it has no ambient request
/// state: <c>MessageContext.Current</c>'s properties and an <c>IHttpContextAccessor</c> are not the publisher's, so decide
/// from the event itself.
/// </para>
/// <para>
/// An exception it throws, or a payload that does not read as the event's type, skips that event's push: the transport
/// logs it (<c>RecipientResolutionFailed</c>, EventId 100, Warning) and pushes the event to nobody. The transport does not
/// throw for it, so the publish, the other transports and the outbox's delivery carry on.
/// </para>
/// </remarks>
public interface IEventRecipientResolver
{
    /// <summary>Names the connections that receive <paramref name="event"/>.</summary>
    /// <param name="eventType">The event's full type name: the name a client listens for.</param>
    /// <param name="event">
    /// The event, deserialized as its own type with <c>ConduitRuntime.JsonOptions</c>; the raw <c>JsonElement</c> payload
    /// when this host's registry does not list the type.
    /// </param>
    /// <param name="cancellationToken">The publish's cancellation token.</param>
    /// <returns>Who receives the event; <see cref="EventRecipients.None"/> pushes it to nobody.</returns>
    public ValueTask<EventRecipients> ResolveAsync(string eventType, object @event, CancellationToken cancellationToken);
}
