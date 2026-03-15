using Conduit.Exceptions;
using Conduit.Handlers;
using Conduit.Sample.Todos.Queries;

namespace Conduit.Sample.Todos.Handlers;

internal sealed class GetTodoByIdHandler(TodoStore store)
    : IQueryHandler<GetTodoByIdQuery, TodoDto>
{
    public Task<TodoDto> HandleAsync(
        GetTodoByIdQuery query,
        CancellationToken cancellationToken)
    {
        var item = store.Get(query.Id)
            ?? throw new NotFoundException($"Todo {query.Id} not found");

        return Task.FromResult(item.ToDto());
    }
}
