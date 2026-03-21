using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class TestEventListener(SpyLog spyLog) : IEventListener<TestEvent>
{
    public Task HandleAsync(TestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
