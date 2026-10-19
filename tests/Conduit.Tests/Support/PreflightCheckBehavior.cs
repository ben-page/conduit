using Conduit.Exceptions;
using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>An inbound check that does what a <see cref="PreflightCommand"/>'s verdict says, and records that it ran.</summary>
public sealed class PreflightCheckBehavior<TRequest, TResult>(PreflightProbe probe)
    : IPipelineBehavior<TRequest, TResult>, IInboundCheckBehavior
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        probe.See("check");
        return (request as PreflightCommand)?.Verdict switch
        {
            "refuse" => throw new PreflightRefusal("locked"),
            "explode" => throw new InvalidOperationException("SELECT * FROM secrets"),
            "translate" => throw new TimeoutException("stale row"),
            _ => next(),
        };
    }
}
