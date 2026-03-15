using Conduit.Handlers;
using Conduit.Sample.Notes.Queries;

namespace Conduit.Sample.Notes.Handlers;

internal sealed class GetNotesHandler(NoteStore store)
    : IQueryHandler<GetNotesQuery, IReadOnlyList<NoteDto>>
{
    public Task<IReadOnlyList<NoteDto>> HandleAsync(
        GetNotesQuery query,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(store.GetAll());
    }
}
