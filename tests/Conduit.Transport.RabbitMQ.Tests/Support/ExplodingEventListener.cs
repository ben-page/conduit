using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class ExplodingEventListener : IEventListener<ExplodingEvent>
{
    public Task HandleAsync(ExplodingEvent @event, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Listener failed intentionally");
    }
}
