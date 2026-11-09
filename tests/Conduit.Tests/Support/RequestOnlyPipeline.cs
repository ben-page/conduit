using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// An open generic behavior with one type parameter: it implements <c>IPipelineBehavior&lt;,&gt;</c>, but the runtime
/// closes every pipeline type over a request AND a result type, so it cannot be registered as a pipeline.
/// </summary>
public class RequestOnlyPipeline<TRequest> : IPipelineBehavior<TRequest, Conduit.Messages.Unit>
{
    public Task<Conduit.Messages.Unit> HandleAsync(TRequest request, Func<Task<Conduit.Messages.Unit>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        return next();
    }
}
