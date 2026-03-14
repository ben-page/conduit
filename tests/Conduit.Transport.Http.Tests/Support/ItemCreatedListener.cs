using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class ItemCreatedListener : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
