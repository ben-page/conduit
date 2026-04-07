using Conduit.Handlers;

namespace Conduit.Resilience.Tests.Support;

/// <summary>
/// Handler for <see cref="RetryCommand"/> that fails transiently on the first
/// <see cref="RetryCommand.FailCount"/> calls, then succeeds.
/// Injects a singleton <see cref="CallTracker"/> so call counts survive across
/// transient handler instantiations during retry.
/// </summary>
public class RetryCommandHandler : ICommandHandler<RetryCommand, string>
{
    private readonly CallTracker callTracker;

    public RetryCommandHandler(CallTracker callTracker)
    {
        this.callTracker = callTracker;
    }

    public Task<string> HandleAsync(RetryCommand command, CancellationToken cancellationToken)
    {
        var callNumber = this.callTracker.IncrementAndGet();
        if (callNumber <= command.FailCount)
        {
            throw new TimeoutException($"Transient failure on call {callNumber}");
        }

        return Task.FromResult("success");
    }
}
