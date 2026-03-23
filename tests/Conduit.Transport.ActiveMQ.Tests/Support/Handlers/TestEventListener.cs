using Conduit.Handlers;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class TestEventListener(SpyLog spyLog) : IEventListener<TestEvent>
{
    public Task HandleAsync(TestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
