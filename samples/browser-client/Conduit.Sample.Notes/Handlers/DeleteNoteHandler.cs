using Conduit.Handlers;
using Conduit.Sample.Notes.Commands;
using Conduit.Sample.Notes.Events;

namespace Conduit.Sample.Notes.Handlers;

internal sealed class DeleteNoteHandler(NoteStore store, IMediator mediator)
    : ICommandHandler<DeleteNoteCommand>
{
    public async Task HandleAsync(DeleteNoteCommand command, CancellationToken cancellationToken)
    {
        store.Remove(command.Id);
        await mediator.PublishAsync(new NoteDeletedEvent(command.Id), cancellationToken);
    }
}
