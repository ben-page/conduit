using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Events;

[ClientVisible]
public record NoteCreatedEvent(Guid Id, string Text) : IEvent;
