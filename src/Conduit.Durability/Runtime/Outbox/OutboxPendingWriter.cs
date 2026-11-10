using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;
using Conduit.Runtime;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// The scoped <see cref="IOutboxPendingWriter"/>. Its write is the one
/// <see cref="OutboxBehavior{TRequest,TResult}"/> runs at the end of every pipeline
/// (<see cref="WriteUnwrittenAsync"/>), so the two cannot drift. <paramref name="options"/> names
/// the dispatch properties each entry carries (<see cref="OutboxOptions.PersistedProperties"/>);
/// null carries none. <paramref name="timeProvider"/> stamps each entry's <see cref="OutboxEntry.CreatedAt"/>;
/// null is <see cref="TimeProvider.System"/>.
/// </summary>
public sealed class OutboxPendingWriter(
    OutboxCapture capture,
    IOutboxStore store,
    IConduitConnectionFactory connectionFactory,
    IOptions<OutboxOptions>? options = null,
    TimeProvider? timeProvider = null) : IOutboxPendingWriter
{
    /// <inheritdoc />
    public Task WritePendingAsync(CancellationToken cancellationToken = default)
        => WriteUnwrittenAsync(
            capture,
            store,
            connectionFactory,
            options?.Value.PersistedProperties,
            timeProvider ?? TimeProvider.System,
            cancellationToken);

    /// <summary>
    /// Writes <paramref name="capture"/>'s unwritten envelopes through the current transaction and
    /// records them as written (<see cref="OutboxCapture.AddWrittenMessages"/>) once the save has
    /// succeeded. Nothing happens when nothing is unwritten or no transaction is active. Each entry
    /// carries the current context's values for the <paramref name="persistedProperties"/> keys, and
    /// <paramref name="timeProvider"/>'s current instant as its creation time.
    /// </summary>
    internal static async Task WriteUnwrittenAsync(
        OutboxCapture capture,
        IOutboxStore store,
        IConduitConnectionFactory connectionFactory,
        IDictionary<string, Type>? persistedProperties,
        TimeProvider timeProvider,
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

        // The publishing dispatch's listed properties (the host's user, say) ride the entry, so its
        // delivery — by the flush below or a relay redelivery — runs under them. The flush gets them in
        // the shape the relay reads back from the column, so the two paths cannot differ.
        var properties = OutboxProperties.Write(context?.Properties, persistedProperties);
        var restoredProperties = OutboxProperties.Read(properties, persistedProperties);

        var createdAt = timeProvider.GetUtcNow().UtcDateTime;
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
                CreatedAt = createdAt,
                Properties = properties,
            });
        }

        await store.SaveAsync(entries, transaction, cancellationToken);
        MessageContext.Observe(new OutboxWritten(entries.Count));

        capture.AddWrittenMessages(
            entries.Select((entry, index) => new WrittenOutboxMessage(
                entry.MessageId,
                entry.ConversationId,
                entry.CausationId,
                captured[index],
                restoredProperties)).ToList());
    }
}
