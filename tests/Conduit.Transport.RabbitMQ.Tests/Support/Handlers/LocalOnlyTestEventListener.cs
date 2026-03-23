using Conduit.Handlers;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.RabbitMQ.Tests.Support.Handlers;

public class LocalOnlyTestEventListener(SpyLog spyLog) : IEventListener<LocalOnlyTestEvent>
{
    public Task HandleAsync(LocalOnlyTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
