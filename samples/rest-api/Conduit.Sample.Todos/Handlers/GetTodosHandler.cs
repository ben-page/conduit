using Conduit.Handlers;
using Conduit.Sample.Todos.Queries;

namespace Conduit.Sample.Todos.Handlers;

internal sealed class GetTodosHandler(TodoStore store)
    : IQueryHandler<GetTodosQuery, IReadOnlyList<TodoDto>>
{
    public Task<IReadOnlyList<TodoDto>> HandleAsync(
        GetTodosQuery query,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(store.GetAll());
    }
}
