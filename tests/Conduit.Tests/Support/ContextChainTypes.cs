using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

public record TopLevelCommand(string Value) : ICommand<string>;

/// <summary>
/// Handles TopLevelCommand by publishing two events.
/// Chain: TopLevelCommand → NestedEventOne + NestedEventTwo
/// </summary>
public class TopLevelHandler(IMediator mediator) : ICommandHandler<TopLevelCommand, string>
{
    public async Task<string> HandleAsync(TopLevelCommand command, CancellationToken cancellationToken)
    {
        await mediator.PublishAsync(new NestedEventOne(command.Value), cancellationToken);
        await mediator.PublishAsync(new NestedEventTwo(command.Value), cancellationToken);
        return command.Value;
    }
}

public record NestedEventOne(string Value) : IEvent;

/// <summary>
/// No-op listener so the pipeline runs for NestedEventOne and captures context.
/// </summary>
public class NestedEventOneListener : IEventListener<NestedEventOne>
{
    public Task HandleAsync(NestedEventOne @event, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public record NestedEventTwo(string Value) : IEvent;

/// <summary>
/// Listens for NestedEventTwo and dispatches a DeeplyNestedCommand.
/// Chain: NestedEventTwo → DeeplyNestedCommand
/// </summary>
public class NestedEventTwoListener(IMediator mediator) : IEventListener<NestedEventTwo>
{
    public async Task HandleAsync(NestedEventTwo @event, CancellationToken cancellationToken)
    {
        await mediator.ExecuteAsync(new DeeplyNestedCommand(@event.Value), cancellationToken);
    }
}

public record DeeplyNestedCommand(string Value) : ICommand<string>;

/// <summary>
/// Handles DeeplyNestedCommand. Terminal node in the dispatch chain.
/// </summary>
public class DeeplyNestedHandler : ICommandHandler<DeeplyNestedCommand, string>
{
    public Task<string> HandleAsync(DeeplyNestedCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(command.Value);
    }
}
