using Conduit.Durability.Persistence.Inbox;
using Conduit.Middleware;
using Conduit.Runtime;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.Inbox;

/// <summary>
/// Releases the inbox record of a dispatch that failed, so the message runs when it next arrives: the broker's
/// redelivery of a message the transport abandoned, a dead-letter replay, which carries the original MessageId
/// (<see cref="IInboundErrorInterceptor"/>, the broker path), or the caller's retry through a trusting HTTP or
/// SignalR edge, such as the outbox relay's next attempt or a resilience retry, under the same MessageId
/// (<see cref="IInboundFailureObserver"/>, the edge path). Without it, the record the gate made before the handler
/// ran would have the next arrival discarded as a duplicate. Registered by <c>AddInbox()</c> under both interfaces.
/// </summary>
/// <remarks>
/// It releases only the id this dispatch's <see cref="InboxInterceptor"/> recorded. A failure before the gate
/// ran (an unreadable body, a gate ahead of the inbox that threw) or a duplicate the gate discarded leaves a record
/// that another delivery made, perhaps one whose handler succeeded. It captures nothing, so it always answers
/// <c>false</c>: whether the message is stored, acknowledged or abandoned is left to the dead letter queue and the
/// transport, as without an inbox.
/// </remarks>
internal sealed class InboxErrorInterceptor : IInboundErrorInterceptor, IInboundFailureObserver
{
    private static readonly Action<ILogger, string, Exception?> LogReleaseFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(330, "InboxReleaseFailed"),
            "Failed to release the inbox record of a failed message of type '{MessageType}'; its redelivery, "
                + "replay or retry is discarded as a duplicate until the record expires");

    private readonly InboxInterceptor gate;
    private readonly IInboxStore store;
    private readonly ILogger<InboxErrorInterceptor> logger;

    public InboxErrorInterceptor(InboxInterceptor gate, IInboxStore store, ILogger<InboxErrorInterceptor> logger)
    {
        this.gate = gate;
        this.store = store;
        this.logger = logger;
    }

    public async Task<bool> TryCaptureAsync(
        string messageType,
        BinaryData body,
        string sourceEndpoint,
        Exception exception,
        string? reference,
        CancellationToken cancellationToken)
    {
        await this.ReleaseAsync(messageType, cancellationToken);
        return false;
    }

    public async ValueTask OnDispatchFailedAsync(string messageType, DispatchError error, CancellationToken cancellationToken)
        => await this.ReleaseAsync(messageType, cancellationToken);

    private async Task ReleaseAsync(string messageType, CancellationToken cancellationToken)
    {
        if (this.gate.AdmittedMessageId is not { } messageId)
        {
            return;
        }

        try
        {
            await this.store.RemoveAsync(messageId, cancellationToken);
        }
#pragma warning disable CA1031 // A release that fails is logged, never a second failure for the transport or edge to settle.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The error path: a failure here is logged, never a second failure for the transport or edge to settle
            LogReleaseFailed(this.logger, messageType, ex);
        }
    }
}
