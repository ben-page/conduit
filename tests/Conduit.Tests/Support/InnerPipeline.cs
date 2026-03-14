using Conduit.Middleware;

namespace Conduit.Tests.Support;

public class InnerPipeline<TRequest, TResult>(PipelineLog log) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        log.Entries.Add("Inner:before");
        var result = await next();
        log.Entries.Add("Inner:after");
        return result;
    }
}
