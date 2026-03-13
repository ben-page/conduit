using Conduit;
using Conduit.Sample.Events;

namespace Conduit.Sample.Handlers;

public class ItemCreatedListener(ILogger<ItemCreatedListener> logger) : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        logger.LogInformation("Item created: {Id} - {Name}", @event.Id, @event.Name);
        return Task.CompletedTask;
    }
}
