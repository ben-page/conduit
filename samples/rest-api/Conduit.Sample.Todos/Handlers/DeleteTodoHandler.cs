using Conduit.Handlers;
using Conduit.Sample.Todos.Commands;

namespace Conduit.Sample.Todos.Handlers;

internal sealed class DeleteTodoHandler(TodoStore store)
    : ICommandHandler<DeleteTodoCommand>
{
    public Task HandleAsync(DeleteTodoCommand command, CancellationToken cancellationToken)
    {
        store.Remove(command.Id);
        return Task.CompletedTask;
    }
}
