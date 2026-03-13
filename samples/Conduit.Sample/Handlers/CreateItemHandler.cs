using Conduit;
using Conduit.Sample.Commands;
using Conduit.Sample.Events;

namespace Conduit.Sample.Handlers;

public class CreateItemHandler(IMediator mediator) : ICommandHandler<CreateItemCommand, Guid>
{
    private static readonly Dictionary<Guid, string> Items = new();

    public async Task<Guid> HandleAsync(CreateItemCommand command, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Items[id] = command.Name;

        await mediator.PublishAsync(new ItemCreatedEvent(id, command.Name), cancellationToken);

        return id;
    }

    public static string? GetItem(Guid id) => Items.TryGetValue(id, out var name) ? name : null;
    public static void DeleteItem(Guid id) => Items.Remove(id);
}
