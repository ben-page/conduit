using Conduit.Handlers;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

public class ItemCreatedListener(EventSpy<ItemCreatedEvent> spy) : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        spy.Record(@event);
        return Task.CompletedTask;
    }
}
