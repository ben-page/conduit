using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class CreateItemHandler(ItemStore store) : ICommandHandler<CreateItemCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateItemCommand command, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        store.Add(id, command.Name);
        return Task.FromResult(id);
    }
}
