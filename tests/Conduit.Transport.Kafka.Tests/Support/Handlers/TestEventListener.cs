using Conduit.Handlers;
using Conduit.Transport.Kafka.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.Kafka.Tests.Support.Handlers;

public class TestEventListener(SpyLog spyLog) : IEventListener<TestEvent>
{
    public Task HandleAsync(TestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
