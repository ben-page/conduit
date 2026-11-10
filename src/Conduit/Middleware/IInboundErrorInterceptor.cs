namespace Conduit.Middleware;

/// <summary>
/// Intercepts inbound messages that could not be handled: a message that failed before it could be
/// dispatched (an unreadable body, a scope or mediator that could not be built), a handler or listener
/// that threw, and a request that answered <c>InternalError</c>. Gives infrastructure (e.g., the dead
/// letter queue) a chance to capture the message for later inspection and replay. A refusal (a
/// <c>ConduitException</c>: not found, conflict, forbidden, ...) is never offered: it is an answer, and
/// a replay would be refused again.
/// </summary>
/// <remarks>
/// This interceptor runs in the transport's error path for all dispatch types
/// (commands, queries, events, fire-and-forget). Implementations should be idempotent
/// and fail gracefully — returning <c>false</c> causes the transport to fall back to
/// its native behavior: abandon (redeliver) a failed dispatch, acknowledge a message that failed
/// before dispatch (a redelivery would fail the same way).
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
    /// <param name="exception">The exception that caused the failure. For a request that answered
    /// <c>InternalError</c>, whose real exception the runtime logged rather than returned, one that names
    /// the answer; <paramref name="reference"/> then finds the log line.</param>
    /// <param name="reference">The support reference of a request that answered <c>InternalError</c>
    /// (<c>DispatchError.Reference</c>): the value its caller was given and the logged exception carries.
    /// Null for every other failure.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// <c>true</c> if the message was captured (the transport should acknowledge);
    /// <c>false</c> if capture was not possible or failed (the transport falls back as described above).
    /// </returns>
    public Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        string? reference,
        CancellationToken cancellationToken);
}
