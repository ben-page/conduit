using Conduit.Middleware;

namespace Conduit.Transport.SignalR.Tests.Support;

/// <summary>
/// A pipeline behavior that throws for a <see cref="BehaviorFailureEvent"/>, outside every listener's own try, and
/// passes anything else through.
/// </summary>
public sealed class ThrowingEventBehavior<TRequest, TResult> : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (request is BehaviorFailureEvent)
        {
            throw new InvalidOperationException("The behavior failed.");
        }

        return next();
    }
}
