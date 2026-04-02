using Conduit.Handlers;
using Conduit.Sample.Todos.Commands;

namespace Conduit.Sample.Todos.Handlers;

/// <inheritdoc />
public sealed class DeleteTodoHandler(TodoStore store)
    : ICommandHandler<DeleteTodoCommand>
{
    /// <inheritdoc />
    public Task HandleAsync(DeleteTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        store.Remove(command.Id);
        return Task.CompletedTask;
    }
}
