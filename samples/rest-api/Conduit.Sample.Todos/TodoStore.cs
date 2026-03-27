using System.Collections.Concurrent;

namespace Conduit.Sample.Todos;

/// <summary>
/// Thread-safe in-memory todo storage.
/// </summary>
public sealed class TodoStore
{
    private readonly ConcurrentDictionary<Guid, TodoItem> items = new();

    /// <summary>Adds a new todo item and returns its identifier.</summary>
    /// <param name="title">The todo title.</param>
    public Guid Add(string title)
    {
        var id = Guid.NewGuid();
        this.items[id] = new TodoItem(id, title, false);
        return id;
    }

    /// <summary>Returns the todo item with the specified identifier, or null if not found.</summary>
    /// <param name="id">The todo identifier.</param>
    public TodoItem? Get(Guid id) =>
        this.items.TryGetValue(id, out var item) ? item : null;

    /// <summary>Returns all todo items.</summary>
    public IReadOnlyList<TodoDto> GetAll() =>
        this.items.Values.Select(i => i.ToDto()).ToList();

    /// <summary>Updates an existing todo item, or returns null if not found.</summary>
    /// <param name="id">The todo identifier.</param>
    /// <param name="title">The updated title.</param>
    /// <param name="isComplete">Whether the todo is complete.</param>
    public TodoItem? Update(Guid id, string title, bool isComplete)
    {
        if (!this.items.TryGetValue(id, out _))
        {
            return null;
        }

        var updated = new TodoItem(id, title, isComplete);
        this.items[id] = updated;
        return updated;
    }

    /// <summary>Removes the todo item with the specified identifier.</summary>
    /// <param name="id">The todo identifier.</param>
    public void Remove(Guid id) => this.items.TryRemove(id, out _);
}

/// <summary>Internal storage record.</summary>
/// <param name="Id">The todo identifier.</param>
/// <param name="Title">The todo title.</param>
/// <param name="IsComplete">Whether the todo is complete.</param>
public record TodoItem(Guid Id, string Title, bool IsComplete)
{
    /// <summary>Converts this item to a public DTO.</summary>
    public TodoDto ToDto() => new(this.Id, this.Title, this.IsComplete);
}

/// <summary>Public DTO returned by queries.</summary>
/// <param name="Id">The todo identifier.</param>
/// <param name="Title">The todo title.</param>
/// <param name="IsComplete">Whether the todo is complete.</param>
public record TodoDto(Guid Id, string Title, bool IsComplete);
