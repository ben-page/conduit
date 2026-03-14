using Conduit.Middleware;

namespace Conduit.Tests.Support;

public class OuterPipeline<TRequest, TResult>(PipelineLog log) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        log.Entries.Add("Outer:before");
        var result = await next();
        log.Entries.Add("Outer:after");
        return result;
    }
}
