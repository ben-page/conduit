using Conduit.Handlers;
using Conduit.Messages;
using Conduit.TestUtils;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>A command whose handler throws <see cref="RetryMeException"/> on its first <paramref name="Failures"/> calls.</summary>
public record FlakyCommand(int Failures) : ICommand<Guid>;

/// <summary>Records a sighting, fails while calls remain, then returns the id of the scope it was resolved from.</summary>
public class FlakyCommandHandler(RetryProbe probe, ScopeProbe scope) : ICommandHandler<FlakyCommand, Guid>
{
    public Task<Guid> HandleAsync(FlakyCommand command, CancellationToken cancellationToken)
    {
        var call = probe.See(scope);
        if (call <= command.Failures)
        {
            throw new RetryMeException($"{nameof(FlakyCommand)} failed on call {call}");
        }

        return Task.FromResult(scope.Id);
    }
}
