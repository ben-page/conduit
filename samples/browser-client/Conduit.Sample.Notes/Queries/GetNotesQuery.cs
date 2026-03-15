using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Sample.Notes.Queries;

[ClientVisible]
public record GetNotesQuery() : IQuery<IReadOnlyList<NoteDto>>;
