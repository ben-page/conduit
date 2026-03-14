using Conduit.Messages;

namespace Conduit.Sample.Notes.Commands;

public record CreateNoteCommand(string Text) : ICommand<Guid>;
