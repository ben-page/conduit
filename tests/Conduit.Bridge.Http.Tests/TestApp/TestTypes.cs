using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Bridge.Http.Tests.TestApp;

public class ItemStore
{
    private readonly Dictionary<Guid, string> items = new();

    public void Add(Guid id, string name) => items[id] = name;

    public string? Get(Guid id) => items.TryGetValue(id, out var name) ? name : null;

    public void Remove(Guid id) => items.Remove(id);
}

public record ItemDto(Guid Id, string Name);

[Dispatchable]
public record CreateItemCommand(string Name) : ICommand<Guid>;

[Dispatchable]
public record DeleteItemCommand(Guid Id) : ICommand;

[Dispatchable]
public record GetItemQuery(Guid Id) : IQuery<ItemDto>;

[Dispatchable]
public record ItemCreatedEvent(Guid Id, string Name) : IEvent;

[Dispatchable]
public record ThrowConflictCommand() : ICommand;

[Dispatchable]
public record ThrowUnauthorizedCommand() : ICommand;

[Dispatchable]
public record ThrowGenericCommand() : ICommand;
