using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Commands;

[ClientVisible]
public record DeleteNoteCommand(Guid Id) : ICommand;
