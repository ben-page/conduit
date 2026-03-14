using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.TestApp;

public class ItemStore
{
    private readonly Dictionary<Guid, string> items = new();

    public void Add(Guid id, string name) => this.items[id] = name;
    public string? Get(Guid id) => this.items.TryGetValue(id, out var name) ? name : null;
    public void Remove(Guid id) => this.items.Remove(id);
}

public class CreateItemHandler(ItemStore store) : ICommandHandler<CreateItemCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateItemCommand command, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        store.Add(id, command.Name);
        return Task.FromResult(id);
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
        var name = store.Get(query.Id) ?? throw new NotFoundException($"Item {query.Id} not found");
        return Task.FromResult(name);
    }
}
