using Conduit.Handlers;

namespace Conduit.Transport.Http.Tests.Support;

public class NoteItemHandler(ItemStore store) : ICommandHandler<NoteItemCommand>
{
    public Task HandleAsync(NoteItemCommand command, CancellationToken cancellationToken)
    {
        store.Add(command.Id, command.Name);
        return Task.CompletedTask;
    }
}
