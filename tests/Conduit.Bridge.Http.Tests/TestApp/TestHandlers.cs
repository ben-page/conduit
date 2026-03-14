using Conduit.Exceptions;
using Conduit.Handlers;

namespace Conduit.Bridge.Http.Tests.TestApp;

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

public class GetItemHandler(ItemStore store) : IQueryHandler<GetItemQuery, ItemDto>
{
    public Task<ItemDto> HandleAsync(GetItemQuery query, CancellationToken cancellationToken)
    {
        var name = store.Get(query.Id)
            ?? throw new NotFoundException($"Item {query.Id} not found");

        return Task.FromResult(new ItemDto(query.Id, name));
    }
}

public class ItemCreatedListener : IEventListener<ItemCreatedEvent>
{
    public Task HandleAsync(ItemCreatedEvent @event, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public class ThrowConflictHandler : ICommandHandler<ThrowConflictCommand>
{
    public Task HandleAsync(ThrowConflictCommand command, CancellationToken cancellationToken)
        => throw new ConflictException("Already exists");
}

public class ThrowUnauthorizedHandler : ICommandHandler<ThrowUnauthorizedCommand>
{
    public Task HandleAsync(ThrowUnauthorizedCommand command, CancellationToken cancellationToken)
        => throw new UnauthorizedException("Access denied");
}

public class ThrowGenericHandler : ICommandHandler<ThrowGenericCommand>
{
    public Task HandleAsync(ThrowGenericCommand command, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Something went wrong");
}
