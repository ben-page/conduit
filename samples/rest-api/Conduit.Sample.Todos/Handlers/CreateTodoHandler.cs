using Conduit.Handlers;
using Conduit.Sample.Todos.Commands;

namespace Conduit.Sample.Todos.Handlers;

/// <inheritdoc />
public sealed class CreateTodoHandler(TodoStore store)
    : ICommandHandler<CreateTodoCommand, Guid>
{
    /// <inheritdoc />
    public Task<Guid> HandleAsync(CreateTodoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var id = store.Add(command.Title);
        return Task.FromResult(id);
    }
}
