using Conduit.Handlers;
using Conduit.TestUtils;

namespace Conduit.Tests.TestApp;

public class ItemStore
{
    private readonly Dictionary<Guid, string> items = new();

    public void Add(Guid id, string name) => this.items[id] = name;
    public string? Get(Guid id) => this.items.TryGetValue(id, out var name) ? name : null;
    public void Remove(Guid id) => this.items.Remove(id);
}

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

public class DeleteItemHandler(ItemStore store) : ICommandHandler<DeleteItemCommand>
{
    public Task HandleAsync(DeleteItemCommand command, CancellationToken cancellationToken)
    {
        store.Remove(command.Id);
        return Task.CompletedTask;
    }
}

public class GetItemHandler(ItemStore store) : IQueryHandler<GetItemQuery, string>
{
    public Task<string> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = store.Get(query.Id) ?? throw new Conduit.Exceptions.NotFoundException($"Item {query.Id} not found");
        return Task.FromResult(name);
    }
}

public class ItemCreatedListener(EventSpy<ItemCreatedEvent> spy) : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        spy.Record(@event);
        return Task.CompletedTask;
    }
}
