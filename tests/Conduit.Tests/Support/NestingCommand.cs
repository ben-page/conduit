using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Tests.Support;

/// <summary>A command whose handler sends an <see cref="InnerCommand"/> through its own injected mediator: a nested dispatch.</summary>
public record NestingCommand(int InnerFailures) : ICommand;

public class NestingCommandHandler(IMediator mediator, RetryProbe probe, AttemptProbe scope)
    : ICommandHandler<NestingCommand>
{
    public async Task HandleAsync(NestingCommand command, CancellationToken cancellationToken)
    {
        probe.See(nameof(NestingCommand), scope);
        await mediator.ExecuteAsync(new InnerCommand(command.InnerFailures), cancellationToken);
    }
}
