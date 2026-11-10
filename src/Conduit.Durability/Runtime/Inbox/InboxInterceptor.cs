using Conduit.Durability.Persistence.Inbox;
using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Durability.Runtime.Inbox;

/// <summary>
/// Inbound gate that checks MessageId against the inbox store for deduplication.
/// If the message has already been processed, returns false to silently discard it.
/// </summary>
/// <remarks>
/// Scoped, so one instance serves one inbound dispatch: it remembers the id it recorded, and
/// <see cref="InboxErrorInterceptor"/> releases that record, and only that one, if the dispatch fails.
/// </remarks>
public sealed class InboxInterceptor(IInboxStore store) : IInboundInterceptor
{
    /// <summary>
    /// The MessageId this gate recorded for its dispatch, or null when it recorded none: no context, a
    /// duplicate, or it never ran (the dispatch failed before its gates).
    /// </summary>
    internal string? AdmittedMessageId { get; private set; }

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

        this.AdmittedMessageId = messageId;
        return true;
    }
}
