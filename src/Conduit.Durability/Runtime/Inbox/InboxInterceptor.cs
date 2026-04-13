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

        if (await store.ExistsAsync(messageId, cancellationToken))
        {
            MessageContext.Observe(new InboxDuplicate());
            return false;
        }

        await store.RecordAsync(messageId, cancellationToken);
        return true;
    }
}
