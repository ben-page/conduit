using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Durability.Tests.Support;

/// <summary>An outbox-captured event whose listener sends a command of its own.</summary>
[FireAndForget]
public record RelayedEvent(int Failures) : IEvent;

/// <summary>Sends a <see cref="FlakyCommand"/> from the background dispatcher's scope, as a listener does.</summary>
public class RelayedEventListener(IMediator mediator) : IEventListener<RelayedEvent>
{
    public Task HandleAsync(RelayedEvent @event, CancellationToken cancellationToken)
        => mediator.ExecuteAsync(new FlakyCommand(@event.Failures), cancellationToken);
}

/// <summary>A command that publishes a <see cref="RelayedEvent"/>, to be captured by the outbox.</summary>
public record RelayCommand(int Failures) : ICommand;

public class RelayCommandHandler(IMediator mediator) : ICommandHandler<RelayCommand>
{
    public Task HandleAsync(RelayCommand command, CancellationToken cancellationToken)
        => mediator.PublishAsync(new RelayedEvent(command.Failures), cancellationToken);
}
