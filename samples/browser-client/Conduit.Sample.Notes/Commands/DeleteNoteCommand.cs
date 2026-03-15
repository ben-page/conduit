using Conduit.Messages;

namespace Conduit.Sample.Notes.Commands;

public record DeleteNoteCommand(Guid Id) : ICommand;
