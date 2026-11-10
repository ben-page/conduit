namespace Conduit.Runtime;

/// <summary>
/// Told when an inbound dispatch that a caller is waiting on failed unexpectedly at the HTTP wire endpoint
/// (<c>MapConduitEndpoint</c>'s <c>/dispatch</c> and <c>/publish</c>) or the SignalR hub (<c>ConduitHub</c>'s
/// <c>Dispatch</c> and <c>Publish</c>): a request that answered <c>InternalError</c>, an event whose listener failed or
/// whose dispatch failed outside its listeners. Gives infrastructure that acted on the dispatch before its handler ran
/// a chance to undo it: the inbox releases the record its gate made, so the caller's retry under the same MessageId
/// runs instead of being discarded as a duplicate.
/// </summary>
/// <remarks>
/// A refusal (a <c>ConduitException</c>: not found, conflict, forbidden, ...) is never reported: it is an answer, and a
/// retry would be refused again. Nor is a dispatch a gate discarded, which did not fail. The edge resolves every
/// registered observer from the dispatch's own scope (the HTTP request's services, the hub invocation's) after the
/// dispatch ended and before the answer is written, calls each in registration order, and logs one that throws (at
/// Warning) without letting it change the answer. A broker transport does not call it: it offers its failures to every
/// <see cref="Middleware.IInboundErrorInterceptor"/> instead, which can also capture the message.
/// </remarks>
public interface IInboundFailureObserver
{
    /// <summary>
    /// Called once for an inbound dispatch that failed unexpectedly.
    /// </summary>
    /// <param name="messageType">The full type name of the message whose dispatch failed.</param>
    /// <param name="error">The answer the caller is given: <c>InternalError</c>, the fixed message, and the support
    /// reference (<see cref="DispatchError.Reference"/>) the failure was logged under.</param>
    /// <param name="cancellationToken">Cancellation token. The edges pass <see cref="CancellationToken.None"/>: the
    /// work runs even when the caller has gone, since the caller's retry is what it is for.</param>
    /// <returns>A task that completes once the observer has acted.</returns>
    public ValueTask OnDispatchFailedAsync(string messageType, DispatchError error, CancellationToken cancellationToken);
}
