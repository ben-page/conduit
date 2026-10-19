using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Jobs.Tests.Support;

/// <summary>A command whose handler throws <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.</summary>
public record FlakyCommand(int Failures) : ICommand;

/// <summary>Records a sighting and fails while calls remain.</summary>
public class FlakyCommandHandler(RetryProbe probe, ScopeProbe scope) : ICommandHandler<FlakyCommand>
{
    public Task HandleAsync(FlakyCommand command, CancellationToken cancellationToken)
    {
        var call = probe.See(scope);
        if (call <= command.Failures)
        {
            throw new RetryMeException($"{nameof(FlakyCommand)} failed on call {call}");
        }

        return Task.CompletedTask;
    }
}
