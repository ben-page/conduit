using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Events;

/// <summary>Raised when a note is deleted.</summary>
/// <param name="Id">The note identifier.</param>
[ClientVisible]
public record NoteDeletedEvent(Guid Id) : IEvent;
