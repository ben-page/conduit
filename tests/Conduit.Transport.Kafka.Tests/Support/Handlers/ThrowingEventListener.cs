using Conduit.Handlers;
using Conduit.Transport.Kafka.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.Kafka.Tests.Support.Handlers;

public class ThrowingEventListener(SpyLog spyLog) : IEventListener<ThrowingTestEvent>
{
    public Task HandleAsync(ThrowingTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        throw new InvalidOperationException("Listener failure");
    }
}
