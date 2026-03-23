using Conduit.Handlers;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.AzureServiceBus.Tests.Support.Handlers;

public class LocalOnlyTestEventListener(SpyLog spyLog) : IEventListener<LocalOnlyTestEvent>
{
    public Task HandleAsync(LocalOnlyTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
