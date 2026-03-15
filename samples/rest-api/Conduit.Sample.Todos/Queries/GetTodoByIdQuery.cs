using Conduit.Messages;

namespace Conduit.Sample.Todos.Queries;

public record GetTodoByIdQuery(Guid Id) : IQuery<TodoDto>;
