using Conduit.Handlers;
using Conduit.Sample.Todos.Commands;

namespace Conduit.Sample.Todos.Handlers;

internal sealed class CreateTodoHandler(TodoStore store)
    : ICommandHandler<CreateTodoCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateTodoCommand command, CancellationToken cancellationToken)
    {
        var id = store.Add(command.Title);
        return Task.FromResult(id);
    }
}
