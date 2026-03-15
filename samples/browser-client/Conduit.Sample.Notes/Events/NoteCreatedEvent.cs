using Conduit.Messages;

namespace Conduit.Sample.Notes.Events;

public record NoteCreatedEvent(Guid Id, string Text) : IEvent;
