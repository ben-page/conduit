using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Events;

[ClientVisible]
public record NoteDeletedEvent(Guid Id) : IEvent;
