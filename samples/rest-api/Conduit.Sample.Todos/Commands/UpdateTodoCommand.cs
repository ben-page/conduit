using Conduit.Messages;

namespace Conduit.Sample.Todos.Commands;

/// <summary>Updates an existing todo item.</summary>
/// <param name="Id">The todo identifier.</param>
/// <param name="Title">The updated title.</param>
/// <param name="IsComplete">Whether the todo is complete.</param>
public record UpdateTodoCommand(Guid Id, string Title, bool IsComplete) : ICommand<TodoDto>;
