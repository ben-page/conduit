using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Commands;

/// <summary>Deletes an existing note by identifier.</summary>
/// <param name="Id">The note identifier.</param>
[ClientVisible]
public record DeleteNoteCommand(Guid Id) : ICommand;
