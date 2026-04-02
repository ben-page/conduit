using Conduit.Handlers;
using Conduit.Sample.Notes.Commands;
using Conduit.Sample.Notes.Events;

namespace Conduit.Sample.Notes.Handlers;

/// <inheritdoc />
public sealed class DeleteNoteHandler(NoteStore store, IMediator mediator)
    : ICommandHandler<DeleteNoteCommand>
{
    /// <inheritdoc />
    public async Task HandleAsync(DeleteNoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        store.Remove(command.Id);
        await mediator.PublishAsync(new NoteDeletedEvent(command.Id), cancellationToken);
    }
}
