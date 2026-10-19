using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>A second inbound check, placed after a behavior that is not one, so a test sees the checks run in the
/// pipeline's order with the other skipped.</summary>
public sealed class SecondPreflightCheckBehavior<TRequest, TResult>(PreflightProbe probe)
    : IPipelineBehavior<TRequest, TResult>, IInboundCheckBehavior
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        probe.See("second check");
        return next();
    }
}
