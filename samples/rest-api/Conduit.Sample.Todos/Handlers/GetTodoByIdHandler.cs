using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Sample.Todos.Queries;

namespace Conduit.Sample.Todos.Handlers;

/// <inheritdoc />
public sealed class GetTodoByIdHandler(TodoStore store)
    : IQueryHandler<GetTodoByIdQuery, TodoDto>
{
    /// <inheritdoc />
    public Task<TodoDto> HandleAsync(
        GetTodoByIdQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var item = store.Get(query.Id)
            ?? throw new NotFoundException($"Todo {query.Id} not found");

        return Task.FromResult(item.ToDto());
    }
}
