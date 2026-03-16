using Conduit.Handlers;

namespace Conduit.Tests.Support;

/// <summary>
/// Listener that dispatches a nested command when an item is deleted.
/// Used to test context propagation through nested dispatch chains.
/// </summary>
public class ItemDeletedListener(IMediator mediator) : IEventListener<ItemDeletedEvent>
{
    public async Task HandleAsync(ItemDeletedEvent @event, CancellationToken cancellationToken)
    {
        await mediator.ExecuteAsync(new EchoCommand("cleanup"), cancellationToken);
    }
}
