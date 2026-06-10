using Conduit.Durability.Persistence.Inbox;
using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Durability.Runtime.Inbox;

/// <summary>
/// Inbound gate that checks MessageId against the inbox store for deduplication.
/// If the message has already been processed, returns false to silently discard it.
/// </summary>
public sealed class InboxInterceptor(IInboxStore store) : IInboundInterceptor
{
    /// <inheritdoc/>
    public async Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        var messageId = MessageContext.Current?.MessageId;
        if (messageId is null)
        {
            return true;
        }

        // Single atomic operation: the insert-or-ignore both records the message and tells us
        // whether it was newly seen. This closes the check-then-act window where two concurrent
        // redeliveries of the same MessageId could both pass an ExistsAsync check and run twice.
        var newlyRecorded = await store.RecordAsync(messageId, cancellationToken);
        if (!newlyRecorded)
        {
            MessageContext.Observe(new InboxDuplicate());
            return false;
        }

        return true;
    }
}
