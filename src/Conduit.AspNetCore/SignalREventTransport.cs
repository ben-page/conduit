using Microsoft.AspNetCore.SignalR;

namespace Conduit.AspNetCore;

public class SignalREventTransport(IHubContext<ConduitHub> hubContext) : IEventTransport
{
    public Task DeliverAsync(IEvent @event, CancellationToken cancellationToken)
    {
        var eventName = @event.GetType().Name;
        return hubContext.Clients.All.SendAsync(eventName, @event, cancellationToken);
    }
}
