using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Sample.Todos.Commands;

namespace Conduit.Sample.Todos.Handlers;

/// <inheritdoc />
public sealed class UpdateTodoHandler(TodoStore store)
    : ICommandHandler<UpdateTodoCommand, TodoDto>
{
    /// <inheritdoc />
    public Task<TodoDto> HandleAsync(UpdateTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var updated = store.Update(command.Id, command.Title, command.IsComplete)
            ?? throw new NotFoundException($"Todo {command.Id} not found");

        return Task.FromResult(updated.ToDto());
    }
}
