using Conduit.Durability.Persistence.Inbox;
using Conduit.Middleware;
using Microsoft.Extensions.Logging;

namespace Conduit.Durability.Runtime.Inbox;

/// <summary>
/// <see cref="IInboundErrorInterceptor"/> that releases the inbox record of a dispatch that failed, so the
/// message runs when it next arrives: the broker's redelivery of a message the transport abandoned, or a
/// dead-letter replay, which carries the original MessageId. Without it, the record the gate made before the
/// handler ran would have the next arrival discarded as a duplicate. Registered by <c>AddInbox()</c>.
/// </summary>
/// <remarks>
/// It releases only the id this dispatch's <see cref="InboxInterceptor"/> recorded. A failure before the gate
/// ran (an unreadable body, a gate ahead of the inbox that threw) or a duplicate the gate discarded leaves a record
/// that another delivery made, perhaps one whose handler succeeded. It captures nothing, so it always answers
/// <c>false</c>: whether the message is stored, acknowledged or abandoned is left to the dead letter queue and the
/// transport, as without an inbox.
/// </remarks>
internal sealed class InboxErrorInterceptor : IInboundErrorInterceptor
{
    private static readonly Action<ILogger, string, Exception?> LogReleaseFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(330, "InboxReleaseFailed"),
            "Failed to release the inbox record of a failed message of type '{MessageType}'; its redelivery or "
                + "replay is discarded as a duplicate until the record expires");

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
        if (this.gate.AdmittedMessageId is not { } messageId)
        {
            return false;
        }

        try
        {
            await this.store.RemoveAsync(messageId, cancellationToken);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            // The transport's error path: a failure here is logged, never a second failure for the transport to settle
            LogReleaseFailed(this.logger, messageType, ex);
        }

        return false;
    }
}
