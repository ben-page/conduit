using Conduit.Handlers;

namespace Conduit.Tests.Support.ContextChain;

/// <summary>
/// No-op listener so the pipeline runs for NestedEventOne and captures context.
/// </summary>
public class NestedEventOneListener : IEventListener<NestedEventOne>
{
    public Task HandleAsync(NestedEventOne @event, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
