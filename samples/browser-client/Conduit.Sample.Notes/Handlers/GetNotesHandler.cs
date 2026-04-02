using Conduit.Handlers;
using Conduit.Sample.Notes.Queries;

namespace Conduit.Sample.Notes.Handlers;

/// <inheritdoc />
public sealed class GetNotesHandler(NoteStore store)
    : IQueryHandler<GetNotesQuery, IReadOnlyList<NoteDto>>
{
    /// <inheritdoc />
    public Task<IReadOnlyList<NoteDto>> HandleAsync(
        GetNotesQuery query,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(store.GetAll());
    }
}
