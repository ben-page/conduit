using Conduit.Messages;

namespace Conduit.Sample.Todos.Commands;

public record CreateTodoCommand(string Title) : ICommand<Guid>;
