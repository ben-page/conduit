using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Queries;

/// <summary>Retrieves all notes.</summary>
[ClientVisible]
public record GetNotesQuery() : IQuery<IReadOnlyList<NoteDto>>;
