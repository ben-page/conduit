using Conduit.Handlers;

namespace Conduit.Tests.Support.ContextChain;

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
