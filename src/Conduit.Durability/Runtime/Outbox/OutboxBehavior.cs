using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.Durability.Persistence.Outbox;
using Conduit.Persistence;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Pipeline behavior that writes captured outbound messages to the outbox table
/// within the handler's database transaction.
/// </summary>
public sealed class OutboxBehavior<TRequest, TResult>(
    OutboxCapture capture,
    IOutboxStore store,
    IConduitConnectionFactory connectionFactory) : IPipelineBehavior<TRequest, TResult>
{
    /// <inheritdoc />
    public async Task<TResult> HandleAsync(
        TRequest request,
        Func<Task<TResult>> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var result = await next();

        var captured = capture.Captured;
        if (captured.Count == 0)
        {
            return result;
        }

        var transaction = connectionFactory.GetCurrentTransaction();
        if (transaction is null)
        {
            return result;
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

        capture.SetWrittenMessages(
            entries.Select((entry, index) => new WrittenOutboxMessage(
                entry.MessageId,
                entry.ConversationId,
                entry.CausationId,
                captured[index])).ToList());

        return result;
    }
}
