using Conduit.Messages;

namespace Conduit.Sample.Todos.Commands;

public record UpdateTodoCommand(Guid Id, string Title, bool IsComplete) : ICommand<TodoDto>;
