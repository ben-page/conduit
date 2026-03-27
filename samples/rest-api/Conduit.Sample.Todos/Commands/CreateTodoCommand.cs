using Conduit.Messages;

namespace Conduit.Sample.Todos.Commands;

/// <summary>Creates a new todo item with the given title.</summary>
/// <param name="Title">The todo title.</param>
public record CreateTodoCommand(string Title) : ICommand<Guid>;
