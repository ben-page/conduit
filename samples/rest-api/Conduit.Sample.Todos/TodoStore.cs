using System.Collections.Concurrent;

namespace Conduit.Sample.Todos;

/// <summary>
/// Thread-safe in-memory todo storage.
/// </summary>
public sealed class TodoStore
{
    private readonly ConcurrentDictionary<Guid, TodoItem> items = new();

    public Guid Add(string title)
    {
        var id = Guid.NewGuid();
        this.items[id] = new TodoItem(id, title, false);
        return id;
    }

    public TodoItem? Get(Guid id) =>
        this.items.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyList<TodoDto> GetAll() =>
        this.items.Values.Select(i => i.ToDto()).ToList();

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

    public void Remove(Guid id) => this.items.TryRemove(id, out _);
}

/// <summary>
/// Internal storage record.
/// </summary>
public record TodoItem(Guid Id, string Title, bool IsComplete)
{
    public TodoDto ToDto() => new(this.Id, this.Title, this.IsComplete);
}

/// <summary>
/// Public DTO returned by queries.
/// </summary>
public record TodoDto(Guid Id, string Title, bool IsComplete);
