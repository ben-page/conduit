using Conduit.Handlers;

namespace Conduit.Tests.Support;

public class CreateItemHandler(IMediator mediator, ItemStore store) : ICommandHandler<CreateItemCommand, Guid>
{
    public async Task<Guid> HandleAsync(CreateItemCommand command, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        store.Add(id, command.Name);
        await mediator.PublishAsync(new ItemCreatedEvent(id, command.Name), cancellationToken);
        return id;
    }
}
