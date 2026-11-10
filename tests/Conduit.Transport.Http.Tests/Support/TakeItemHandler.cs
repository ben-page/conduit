using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class TakeItemHandler(ItemStore store) : ICommandHandler<TakeItemCommand, string?>
{
    public Task<string?> HandleAsync(TakeItemCommand command, CancellationToken cancellationToken)
    {
        var name = store.Get(command.Id);
        store.Remove(command.Id);
        return Task.FromResult(name);
    }
}
