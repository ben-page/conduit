using Conduit.Messages;

namespace Conduit.Handlers;

/// <summary>
/// Handles a published event. Multiple listeners may handle the same event type.
/// Each listener runs independently with continue-on-failure semantics.
/// </summary>
/// <typeparam name="TEvent">The event type.</typeparam>
public interface IEventListener<TEvent> where TEvent : IEvent
{
    /// <summary>
    /// Handles the event.
    /// </summary>
    /// <param name="event">The published event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
