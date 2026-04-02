using Conduit.Handlers;
using Conduit.Sample.Notes.Commands;
using Conduit.Sample.Notes.Events;

namespace Conduit.Sample.Notes.Handlers;

/// <inheritdoc />
public sealed class CreateNoteHandler(NoteStore store, IMediator mediator)
    : ICommandHandler<CreateNoteCommand, Guid>
{
    /// <inheritdoc />
    public async Task<Guid> HandleAsync(CreateNoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var id = store.Add(command.Text);
        await mediator.PublishAsync(new NoteCreatedEvent(id, command.Text), cancellationToken);
        return id;
    }
}
