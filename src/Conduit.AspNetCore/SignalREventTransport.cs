using Microsoft.AspNetCore.SignalR;

namespace Conduit.AspNetCore;

/// <summary>
/// Delivers published events to connected SignalR clients via the <see cref="ConduitHub"/>.
/// The event type name is used as the SignalR method name.
/// </summary>
public class SignalREventTransport(IHubContext<ConduitHub> hubContext) : IEventTransport
{
    /// <inheritdoc />
    public Task DeliverAsync(IEvent @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var eventName = @event.GetType().Name;
        return hubContext.Clients.All.SendAsync(eventName, @event, cancellationToken);
    }
}
