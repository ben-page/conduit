using Conduit.Handlers;
using Conduit.Messages;

namespace Conduit.Durability.Tests.Support;

public record CountCommand(string Label) : ICommand<int>;

public class CountHandler : ICommandHandler<CountCommand, int>
{
    private int count;

    public Task<int> HandleAsync(CountCommand command, CancellationToken cancellationToken)
    {
        return Task.FromResult(Interlocked.Increment(ref this.count));
    }
}
