using Conduit.Messages;

namespace Conduit.Sample.Todos.Commands;

public record DeleteTodoCommand(Guid Id) : ICommand;
