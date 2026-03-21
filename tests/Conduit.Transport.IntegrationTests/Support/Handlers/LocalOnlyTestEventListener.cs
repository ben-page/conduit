using Conduit.Handlers;
using Conduit.Transport.IntegrationTests.Support.Contracts;

namespace Conduit.Transport.IntegrationTests.Support.Handlers;

public class LocalOnlyTestEventListener(SpyLog spyLog) : IEventListener<LocalOnlyTestEvent>
{
    public Task HandleAsync(LocalOnlyTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
