using Conduit.Handlers;

namespace Conduit.Transport.RabbitMQ.Tests.Support;

public class FailingEventListener : IEventListener<FailingEvent>
{
    public Task HandleAsync(FailingEvent @event, CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("Listener failed intentionally");
    }
}
