using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>A pipeline behavior that is not an inbound check (a transaction, an audit): a preflight never runs it.</summary>
public sealed class PreflightOtherBehavior<TRequest, TResult>(PreflightProbe probe) : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        probe.See("other");
        return next();
    }
}
