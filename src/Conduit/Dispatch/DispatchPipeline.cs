using Conduit.Exceptions;
using Microsoft.Extensions.Logging;

namespace Conduit.Dispatch;

/// <summary>
/// Builds an interceptor chain and executes dispatch requests or events.
/// Used by transport packages (HTTP endpoints, SignalR hubs) to process
/// inbound messages through the interceptor pipeline.
/// </summary>
public static partial class DispatchPipeline
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception dispatching '{Type}'")]
    private static partial void LogDispatchError(ILogger logger, Exception ex, string type);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception publishing event '{Type}'")]
    private static partial void LogPublishError(ILogger logger, Exception ex, string type);

    /// <summary>
    /// Chains interceptors and dispatches a request through the router.
    /// Returns <see cref="DispatchResult{T}"/> — Ok on success,
    /// Fail with error code on <see cref="ConduitException"/>,
    /// Fail with "InternalError" on unhandled exceptions.
    /// </summary>
    public static async Task<DispatchResult<object>> ExecuteRequestAsync(
        DispatchEnvelope envelope,
        IEnumerable<IDispatchInterceptor> interceptors,
        IDispatchRouter router,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(logger);

        Func<Task<object?>> chain = () =>
            router.DispatchRequestAsync(envelope.Type, envelope.Payload, cancellationToken);

        foreach (var interceptor in interceptors.Reverse())
        {
            var next = chain;
            chain = () => interceptor.InterceptRequestAsync(envelope, next, cancellationToken);
        }

        try
        {
            var result = await chain();
            return DispatchResult<object>.Ok(result);
        }
        catch (ConduitException ex)
        {
            return DispatchResult<object>.Fail(ex.Code, ex.Message);
        }

#pragma warning disable CA1031 // Intentional catch-all at transport boundary
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogDispatchError(logger, ex, envelope.Type);
            return DispatchResult<object>.Fail("InternalError", "An unexpected error occurred");
        }
    }

    /// <summary>
    /// Chains interceptors and dispatches an event through the router.
    /// Swallows all exceptions (logs them). Events are fire-and-forget.
    /// </summary>
    public static async Task ExecuteEventAsync(
        DispatchEnvelope envelope,
        IEnumerable<IDispatchInterceptor> interceptors,
        IDispatchRouter router,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(interceptors);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(logger);

        Func<Task> chain = () =>
            router.DispatchEventAsync(envelope.Type, envelope.Payload, cancellationToken);

        foreach (var interceptor in interceptors.Reverse())
        {
            var next = chain;
            chain = () => interceptor.InterceptEventAsync(envelope, next, cancellationToken);
        }

        try
        {
            await chain();
        }

#pragma warning disable CA1031 // Intentional catch-all at transport boundary
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogPublishError(logger, ex, envelope.Type);
        }
    }
}
