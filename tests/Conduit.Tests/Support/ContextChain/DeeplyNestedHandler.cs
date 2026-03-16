using Conduit.Handlers;

namespace Conduit.Tests.Support.ContextChain;

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
