using Conduit.Messages;

namespace Conduit.Sample.Notes.Queries;

public record GetNotesQuery() : IQuery<IReadOnlyList<NoteDto>>;
