using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

namespace Conduit.Transport.ActiveMQ.Tests.Support.Handlers;

public class FireAndForgetEventListener(SpyLog spyLog) : IEventListener<FireAndForgetEvent>
{
    public Task HandleAsync(FireAndForgetEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
