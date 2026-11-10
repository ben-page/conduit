namespace Conduit.Middleware;

/// <summary>
/// Intercepts inbound messages that could not be handled: a message that failed before it could be
/// dispatched (an unreadable body, a scope or mediator that could not be built), an event this host cannot
/// read, a handler or listener that threw, and a request that answered <c>InternalError</c>. Gives
/// infrastructure a chance to act on the failure: the dead letter queue captures the message for later
/// inspection and replay, the inbox releases its record so the message runs when it next arrives. A refusal
/// (a <c>ConduitException</c>: not found, conflict, forbidden, ...) is never offered: it is an answer, and a
/// replay would be refused again.
/// </summary>
/// <remarks>
/// This interceptor runs in the transport's error path for all dispatch types
/// (commands, queries, events, fire-and-forget). The transport offers each failure to every registered
/// interceptor, in registration order, from the failed dispatch's scope (a fresh scope for a message that
/// failed before dispatch); one that throws is logged and the others still run. Implementations should be
/// idempotent and fail gracefully. The message counts as captured, and the transport acknowledges it once,
/// when any interceptor returns <c>true</c>; when none does, the transport falls back to its native behavior:
/// abandon (redeliver) a failed dispatch, acknowledge a message that failed before dispatch or an event this host
/// cannot read (a redelivery would fail the same way).
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
    /// <c>false</c> if it captured nothing, by design or because capture failed (unless another interceptor
    /// captured it, the transport falls back as described above).
    /// </returns>
    public Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        string? reference,
        CancellationToken cancellationToken);
}
