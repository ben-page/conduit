using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// A pipeline behavior for the dispatch retry tests: it records a sighting at <c>{request type}.behavior</c> with the
/// scoped <see cref="AttemptProbe"/> it was resolved with, so a test sees each attempt resolve the pipeline afresh.
/// </summary>
public sealed class RetryProbeBehavior<TRequest, TResult>(RetryProbe probe, AttemptProbe scope)
    : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(TRequest request, Func<Task<TResult>> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        probe.See($"{typeof(TRequest).Name}.behavior", scope);
        return next();
    }
}
