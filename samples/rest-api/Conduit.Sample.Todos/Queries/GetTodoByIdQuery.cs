using Conduit.Messages;

namespace Conduit.Sample.Todos.Queries;

/// <summary>Retrieves a single todo item by identifier.</summary>
/// <param name="Id">The todo identifier.</param>
public record GetTodoByIdQuery(Guid Id) : IQuery<TodoDto>;
