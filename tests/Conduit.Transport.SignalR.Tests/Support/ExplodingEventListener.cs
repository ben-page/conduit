using Conduit.Handlers;

namespace Conduit.Transport.SignalR.Tests.Support;

public class ExplodingEventListener : IEventListener<ExplodingEvent>
{
    public Task HandleAsync(ExplodingEvent @event, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Event handler exploded!");
    }
}
