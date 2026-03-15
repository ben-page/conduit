using Conduit.Messages;

namespace Conduit.Sample.Todos.Queries;

public record GetTodosQuery() : IQuery<IReadOnlyList<TodoDto>>;
