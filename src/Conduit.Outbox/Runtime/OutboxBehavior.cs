using Conduit.Middleware;
using Conduit.Outbox.Store;
using Conduit.Runtime;

namespace Conduit.Outbox.Runtime;

/// <summary>
/// Pipeline behavior that writes captured outbound messages to the outbox table
/// within the handler's database transaction.
/// </summary>
public sealed class OutboxBehavior<TRequest, TResult>(
    OutboxCapture capture,
    IOutboxStore store,
    IOutboxConnectionFactory connectionFactory) : IPipelineBehavior<TRequest, TResult>
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
        var entries = new List<OutboxEntry>(captured.Count);
        foreach (var envelope in captured)
        {
            entries.Add(new OutboxEntry
            {
                MessageId = context?.MessageId ?? Guid.NewGuid().ToString("N"),
                ConversationId = context?.ConversationId ?? Guid.NewGuid().ToString("N"),
                CausationId = context?.CausationId,
                Type = envelope.Type,
                Payload = envelope.Payload.GetRawText(),
                CreatedAt = DateTime.UtcNow,
            });
        }

        await store.SaveAsync(entries, transaction, cancellationToken);
        return result;
    }
}
