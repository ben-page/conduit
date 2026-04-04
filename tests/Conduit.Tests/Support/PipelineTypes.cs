using Conduit.Middleware;

namespace Conduit.Tests.Support;

public class PipelineLog
{
    public List<string> Entries { get; } = [];
}

public class LoggingPipeline<TRequest, TResult>(PipelineLog log) : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        log.Entries.Add("Pipeline:before");
        var result = await next();
        log.Entries.Add("Pipeline:after");
        return result;
    }
}
