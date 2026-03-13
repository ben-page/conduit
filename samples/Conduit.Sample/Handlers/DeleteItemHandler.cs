using Conduit;
using Conduit.Sample.Commands;

namespace Conduit.Sample.Handlers;

public class DeleteItemHandler : ICommandHandler<DeleteItemCommand>
{
    public Task HandleAsync(DeleteItemCommand command, CancellationToken cancellationToken)
    {
        CreateItemHandler.DeleteItem(command.Id);
        return Task.CompletedTask;
    }
}
