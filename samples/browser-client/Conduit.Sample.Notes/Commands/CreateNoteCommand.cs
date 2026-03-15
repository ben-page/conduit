using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Commands;

[ClientVisible]
public record CreateNoteCommand(string Text) : ICommand<Guid>;
