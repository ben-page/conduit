using Conduit.Handlers;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

using Conduit.TestUtils;
namespace Conduit.Transport.AmazonSqs.Tests.Support.Handlers;

public class LocalOnlyTestEventListener(SpyLog spyLog) : IEventListener<LocalOnlyTestEvent>
{
    public Task HandleAsync(LocalOnlyTestEvent @event, CancellationToken cancellationToken)
    {
        spyLog.Record(@event);
        return Task.CompletedTask;
    }
}
