using System.Collections.Concurrent;
using Conduit.Attributes;

namespace Conduit.Sample.Notes;

/// <summary>
/// Thread-safe in-memory note storage.
/// </summary>
public sealed class NoteStore
{
    private readonly ConcurrentDictionary<Guid, string> notes = new();

    public Guid Add(string text)
    {
        var id = Guid.NewGuid();
        this.notes[id] = text;
        return id;
    }

    public bool Remove(Guid id) => this.notes.TryRemove(id, out _);

    public IReadOnlyList<NoteDto> GetAll() =>
        this.notes.Select(kvp => new NoteDto(kvp.Key, kvp.Value)).ToList();
}

[ClientVisible]
public record NoteDto(Guid Id, string Text);
