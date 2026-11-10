using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>Waits at the <see cref="DispatchGate"/> before running the rest of the pipeline.</summary>
public class GatedPipeline<TRequest, TResult>(DispatchGate gate) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        await gate.PassAsync();
        return await next();
    }
}
