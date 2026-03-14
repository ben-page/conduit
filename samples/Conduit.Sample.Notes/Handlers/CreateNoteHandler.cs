using Conduit.Handlers;
using Conduit.Sample.Notes.Commands;
using Conduit.Sample.Notes.Events;

namespace Conduit.Sample.Notes.Handlers;

internal sealed class CreateNoteHandler(NoteStore store, IMediator mediator)
    : ICommandHandler<CreateNoteCommand, Guid>
{
    public async Task<Guid> HandleAsync(CreateNoteCommand command, CancellationToken cancellationToken)
    {
        var id = store.Add(command.Text);
        await mediator.PublishAsync(new NoteCreatedEvent(id, command.Text), cancellationToken);
        return id;
    }
}
