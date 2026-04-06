using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.Kafka.Tests.Support.Contracts;

namespace Conduit.Transport.Kafka.Tests.Support.Handlers;

public class FireAndForgetEventListener(SpyLog spyLog) : IEventListener<FireAndForgetEvent>
{
    public Task HandleAsync(FireAndForgetEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
