using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class ThrowingEventListener(SpyLog spyLog) : IEventListener<ThrowingTestEvent>
{
    public Task HandleAsync(ThrowingTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        throw new InvalidOperationException("Listener failure");
    }
}
