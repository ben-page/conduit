using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

/// <summary>
/// The command <see cref="NestingCommand"/> and <see cref="IndependentCommand"/> send from their handlers. Its handler
/// throws <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.
/// </summary>
public record InnerCommand(int Failures) : ICommand;

public class InnerCommandHandler(RetryProbe probe, AttemptProbe scope) : ICommandHandler<InnerCommand>
{
    public Task HandleAsync(InnerCommand command, CancellationToken cancellationToken)
    {
        var call = probe.See(nameof(InnerCommand), scope);
        if (call <= command.Failures)
        {
            throw new RetryMeException($"{nameof(InnerCommand)} failed on call {call}");
        }

        return Task.CompletedTask;
    }
}
