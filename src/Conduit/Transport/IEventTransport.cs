using Conduit.Messages;

namespace Conduit.Transport;

/// <summary>
/// Transports published events to external consumers (e.g. SignalR clients).
/// Register implementations to push events beyond the in-process boundary.
/// </summary>
public interface IEventTransport
{
    /// <summary>
    /// Delivers the event to external consumers.
    /// </summary>
    /// <param name="event">The event to deliver.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task DeliverAsync(IEvent @event, CancellationToken cancellationToken);
}
