using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Sample.Todos.Commands;

namespace Conduit.Sample.Todos.Handlers;

internal sealed class UpdateTodoHandler(TodoStore store)
    : ICommandHandler<UpdateTodoCommand, TodoDto>
{
    public Task<TodoDto> HandleAsync(UpdateTodoCommand command, CancellationToken cancellationToken)
    {
        var updated = store.Update(command.Id, command.Title, command.IsComplete)
            ?? throw new NotFoundException($"Todo {command.Id} not found");

        return Task.FromResult(updated.ToDto());
    }
}
