using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

public record CreateItemCommand(string Name) : ICommand<Guid>;

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

public record DeleteItemCommand(Guid Id) : ICommand;

public class DeleteItemHandler(ItemStore store) : ICommandHandler<DeleteItemCommand>
{
    public Task HandleAsync(DeleteItemCommand command, CancellationToken cancellationToken)
    {
        store.Remove(command.Id);
        return Task.CompletedTask;
    }
}

public record GetItemQuery(Guid Id) : IQuery<string>;

public class GetItemHandler(ItemStore store) : IQueryHandler<GetItemQuery, string>
{
    public Task<string> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = store.Get(query.Id) ?? throw new Exceptions.NotFoundException($"Item {query.Id} not found");
        return Task.FromResult(name);
    }
}

public record ItemCreatedEvent(Guid Id, string Name) : IEvent;

public class ItemCreatedListener(EventSpy<ItemCreatedEvent> spy) : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        spy.Record(@event);
        return Task.CompletedTask;
    }
}
