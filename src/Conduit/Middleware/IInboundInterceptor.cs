using Conduit.Runtime;

namespace Conduit.Middleware;

/// <summary>
/// Pre-deserialization gate for inbound wire messages. Registered via DI and
/// consulted by <see cref="ConduitRuntime"/> before the message payload is
/// deserialized. Return false to reject the message (silently discarded).
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
