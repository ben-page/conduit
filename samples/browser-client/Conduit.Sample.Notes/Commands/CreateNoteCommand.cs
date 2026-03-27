using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Commands;

/// <summary>Creates a new note with the given text.</summary>
/// <param name="Text">The note content.</param>
[ClientVisible]
public record CreateNoteCommand(string Text) : ICommand<Guid>;
