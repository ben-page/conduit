using Conduit.Handlers;

namespace Conduit.Bridge.Http.Tests.Support;

public class ItemCreatedListener : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
