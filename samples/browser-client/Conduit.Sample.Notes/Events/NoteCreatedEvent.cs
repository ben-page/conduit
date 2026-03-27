using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Events;

/// <summary>Raised when a new note is created.</summary>
/// <param name="Id">The note identifier.</param>
/// <param name="Text">The note content.</param>
[ClientVisible]
public record NoteCreatedEvent(Guid Id, string Text) : IEvent;
