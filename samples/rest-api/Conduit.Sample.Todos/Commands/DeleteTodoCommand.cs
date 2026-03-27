using Conduit.Messages;

namespace Conduit.Sample.Todos.Commands;

/// <summary>Deletes a todo item by identifier.</summary>
/// <param name="Id">The todo identifier.</param>
public record DeleteTodoCommand(Guid Id) : ICommand;
