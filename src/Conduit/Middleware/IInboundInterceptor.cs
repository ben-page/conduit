using Conduit.Runtime;

namespace Conduit.Middleware;

/// <summary>
/// Pre-deserialization gate for inbound wire messages. Registered via DI and
/// consulted by <see cref="ConduitRuntime"/> before the message payload is
/// deserialized. There are two ways to reject a message:
/// <list type="bullet">
/// <item><description>Return false to silently discard the message — no error reply
/// is sent (as the inbox dedup interceptor does for already-processed messages).</description></item>
/// <item><description>Throw a <see cref="Conduit.Exceptions.ConduitException"/>
/// (e.g. <see cref="Conduit.Exceptions.UnauthorizedException"/>) to reject with an
/// error reply — the runtime maps it to a failing <c>DispatchResult</c> with the
/// exception's error code (as <c>TypeFilterInterceptor</c> does for disallowed types).</description></item>
/// </list>
/// <see cref="MessageContext.Current"/> is available for accessing MessageId
/// and correlation IDs.
/// </summary>
public interface IInboundInterceptor
{
    /// <summary>
    /// Determines whether an inbound message should be processed.
    /// </summary>
    /// <param name="messageType">The fully-qualified message type name from transport headers.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True to proceed with processing; false to reject and discard.</returns>
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken);
}
