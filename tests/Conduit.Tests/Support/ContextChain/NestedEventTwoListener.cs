using Conduit.Handlers;

namespace Conduit.Tests.Support.ContextChain;

/// <summary>
/// Listens for NestedEventTwo and dispatches a DeeplyNestedCommand.
/// Chain: NestedEventTwo → DeeplyNestedCommand
/// </summary>
public class NestedEventTwoListener(IMediator mediator) : IEventListener<NestedEventTwo>
{
    public async Task HandleAsync(NestedEventTwo @event, CancellationToken cancellationToken)
    {
        await mediator.ExecuteAsync(new DeeplyNestedCommand(@event.Value), cancellationToken);
    }
}
