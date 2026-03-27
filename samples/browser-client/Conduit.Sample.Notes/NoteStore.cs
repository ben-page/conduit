using System.Collections.Concurrent;
using Conduit.Attributes;

namespace Conduit.Sample.Notes;

/// <summary>
/// Thread-safe in-memory note storage.
/// </summary>
public sealed class NoteStore
{
    private readonly ConcurrentDictionary<Guid, string> notes = new();

    /// <summary>Adds a new note and returns its identifier.</summary>
    /// <param name="text">The note content.</param>
    public Guid Add(string text)
    {
        var id = Guid.NewGuid();
        this.notes[id] = text;
        return id;
    }

    /// <summary>Removes the note with the specified identifier.</summary>
    /// <param name="id">The note identifier.</param>
    public bool Remove(Guid id) => this.notes.TryRemove(id, out _);

    /// <summary>Returns all notes.</summary>
    public IReadOnlyList<NoteDto> GetAll() =>
        this.notes.Select(kvp => new NoteDto(kvp.Key, kvp.Value)).ToList();
}

/// <summary>Represents a note returned by queries.</summary>
/// <param name="Id">The note identifier.</param>
/// <param name="Text">The note content.</param>
[ClientVisible]
public record NoteDto(Guid Id, string Text);
