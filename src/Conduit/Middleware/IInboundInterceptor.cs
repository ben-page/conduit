using Conduit.Runtime;

namespace Conduit.Middleware;

/// <summary>
/// Intercepts inbound messages arriving from transports. Registered via DI and chained
/// in reverse registration order by the generated mediator — each interceptor
/// calls <c>next</c> to continue the pipeline or short-circuits by returning/throwing.
/// Used for access control, logging, or other cross-cutting concerns
/// on messages arriving from transports (HTTP, SignalR).
/// </summary>
public interface IInboundInterceptor
{
    /// <summary>
    /// Intercepts a command or query dispatch.
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing type and payload.</param>
    /// <param name="next">Delegate to invoke the next interceptor or the wire dispatcher.</param>
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
    /// <param name="next">Delegate to invoke the next interceptor or the wire dispatcher.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task InterceptEventAsync(
        DispatchEnvelope envelope,
        Func<Task> next,
        CancellationToken cancellationToken);
}
