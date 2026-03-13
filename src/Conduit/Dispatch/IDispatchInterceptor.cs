namespace Conduit.Dispatch;

/// <summary>
/// Intercepts dispatch calls before they reach the <see cref="IDispatchRouter"/>.
/// Registered via DI and chained in order — each interceptor calls <c>next</c>
/// to continue the pipeline or short-circuits by returning/throwing.
/// </summary>
public interface IDispatchInterceptor
{
    /// <summary>
    /// Intercepts a command or query dispatch.
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing type and payload.</param>
    /// <param name="next">Delegate to invoke the next interceptor or the router.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The handler result, or <see langword="null"/> for void commands.</returns>
    public Task<object?> InterceptRequestAsync(
        DispatchEnvelope envelope,
        Func<Task<object?>> next,
        CancellationToken cancellationToken);

    /// <summary>
    /// Intercepts an event dispatch.
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing type and payload.</param>
    /// <param name="next">Delegate to invoke the next interceptor or the router.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken);
}
