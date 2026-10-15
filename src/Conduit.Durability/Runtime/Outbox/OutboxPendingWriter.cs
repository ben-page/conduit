using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Conduit.Runtime;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// The scoped <see cref="IOutboxPendingWriter"/>. Its write is the one
/// <see cref="OutboxBehavior{TRequest,TResult}"/> runs at the end of every pipeline
/// (<see cref="WriteUnwrittenAsync"/>), so the two cannot drift.
/// </summary>
public sealed class OutboxPendingWriter(
    OutboxCapture capture,
    IOutboxStore store,
    IConduitConnectionFactory connectionFactory) : IOutboxPendingWriter
{
    /// <inheritdoc />
    public Task WritePendingAsync(CancellationToken cancellationToken = default)
        => WriteUnwrittenAsync(capture, store, connectionFactory, cancellationToken);

    /// <summary>
    /// Writes <paramref name="capture"/>'s unwritten envelopes through the current transaction and
    /// records them as written (<see cref="OutboxCapture.AddWrittenMessages"/>) once the save has
    /// succeeded. Nothing happens when nothing is unwritten or no transaction is active.
    /// </summary>
    internal static async Task WriteUnwrittenAsync(
        OutboxCapture capture,
        IOutboxStore store,
        IConduitConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var captured = capture.Unwritten;
        if (captured.Count == 0)
        {
            return;
        }

        var transaction = connectionFactory.GetCurrentTransaction();
        if (transaction is null)
        {
            return;
        }

        var context = MessageContext.Current;

        // The triggering message (the one currently being handled) becomes the CAUSE of every
        // outbound message it emits. Each emitted message gets its OWN fresh MessageId so that a
        // handler producing several outbound messages does not stamp them all with one shared id —
        // which would make a downstream inbox dedup all-but-one as duplicates (silent message loss)
        // and make ClaimByMessageIdsAsync ambiguous (multiple rows per id). ConversationId is
        // inherited so the whole chain stays correlated.
        var conversationId = context?.ConversationId ?? Guid.NewGuid().ToString("N");
        var causationId = context?.MessageId;

        var entries = new List<OutboxEntry>(captured.Count);
        foreach (var envelope in captured)
        {
            entries.Add(new OutboxEntry
            {
                MessageId = Guid.NewGuid().ToString("N"),
                ConversationId = conversationId,
                CausationId = causationId,
                Type = envelope.Type,
                Payload = envelope.Payload.GetRawText(),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await store.SaveAsync(entries, transaction, cancellationToken);
        MessageContext.Observe(new OutboxWritten(entries.Count));

        capture.AddWrittenMessages(
            entries.Select((entry, index) => new WrittenOutboxMessage(
                entry.MessageId,
                entry.ConversationId,
                entry.CausationId,
                captured[index])).ToList());
    }
}
