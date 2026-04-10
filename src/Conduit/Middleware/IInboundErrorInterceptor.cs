namespace Conduit.Middleware;

/// <summary>
/// Intercepts inbound message failures after dispatch has failed.
/// Called by transports when a handler throws, giving infrastructure (e.g., dead letter queue)
/// a chance to capture the failed message for later inspection and replay.
/// </summary>
/// <remarks>
/// This interceptor runs in the transport's error path for all dispatch types
/// (commands, queries, events, fire-and-forget). Implementations should be idempotent
/// and fail gracefully — returning <c>false</c> causes the transport to fall back to
/// its native abandon/redeliver behavior.
/// </remarks>
public interface IInboundErrorInterceptor
{
    /// <summary>
    /// Attempts to capture a failed inbound message.
    /// </summary>
    /// <param name="messageType">The full type name of the failed message.</param>
    /// <param name="body">The raw message body as it arrived from the transport.</param>
    /// <param name="sourceEndpoint">
    /// The endpoint name the message arrived through. Must match the keyed DI
    /// registration of <c>IConduitTransport</c> so replay can resolve it.
    /// </param>
    /// <param name="exception">The exception that caused the dispatch to fail.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the message was captured (the transport should acknowledge);
    /// <c>false</c> if capture was not possible or failed (the transport should fall back to abandon).
    /// </returns>
    public Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        CancellationToken cancellationToken);
}
