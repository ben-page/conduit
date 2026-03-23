using Conduit.Handlers;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

public class TestEventListener(SpyLog spyLog) : IEventListener<TestEvent>
{
    public Task HandleAsync(TestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
