using Conduit.Messages;

namespace Conduit.Sample.Todos.Queries;

/// <summary>Retrieves all todo items.</summary>
public record GetTodosQuery() : IQuery<IReadOnlyList<TodoDto>>;
