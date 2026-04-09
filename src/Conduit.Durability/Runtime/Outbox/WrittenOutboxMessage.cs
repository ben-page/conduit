using Conduit.Runtime;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Correlates an outbox entry's MessageContext fields with its in-memory
/// <see cref="DispatchEnvelope"/>. Created by <see cref="OutboxBehavior{TRequest,TResult}"/>
/// and consumed by <see cref="OutboxCapture"/> during flush.
/// </summary>
internal sealed record WrittenOutboxMessage(
    string MessageId,
    string ConversationId,
    string? CausationId,
    DispatchEnvelope Envelope);
