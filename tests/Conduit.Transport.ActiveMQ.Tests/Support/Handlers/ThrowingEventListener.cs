using Conduit.Handlers;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class ThrowingEventListener(SpyLog spyLog) : IEventListener<ThrowingTestEvent>
{
    public Task HandleAsync(ThrowingTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        throw new InvalidOperationException("Listener failure");
    }
}
