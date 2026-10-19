using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

/// <summary>A void command whose handler throws <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.</summary>
public record FlakyVoidCommand(int Failures) : ICommand;

public class FlakyVoidCommandHandler(RetryProbe probe, AttemptProbe scope) : ICommandHandler<FlakyVoidCommand>
{
    public Task HandleAsync(FlakyVoidCommand command, CancellationToken cancellationToken)
    {
        var call = probe.See(nameof(FlakyVoidCommand), scope);
        if (call <= command.Failures)
        {
            throw new RetryMeException($"{nameof(FlakyVoidCommand)} failed on call {call}");
        }

        return Task.CompletedTask;
    }
}
