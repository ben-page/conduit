using Conduit.Messages;

namespace Conduit.Sample.Notes.Events;

public record NoteDeletedEvent(Guid Id) : IEvent;
