using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Tests.Support;

/// <summary>
/// A <see cref="FireAndForgetAttribute"/> command whose handler throws <see cref="RetryMeException"/> on its first
/// <paramref name="Failures"/> calls.
/// </summary>
[FireAndForget]
public record FlakyFireAndForgetCommand(int Failures) : ICommand;

public class FlakyFireAndForgetCommandHandler(RetryProbe probe, AttemptProbe scope)
    : ICommandHandler<FlakyFireAndForgetCommand>
{
    public Task HandleAsync(FlakyFireAndForgetCommand command, CancellationToken cancellationToken)
    {
        var call = probe.See(nameof(FlakyFireAndForgetCommand), scope);
        if (call <= command.Failures)
        {
            throw new RetryMeException($"{nameof(FlakyFireAndForgetCommand)} failed on call {call}");
        }

        return Task.CompletedTask;
    }
}
