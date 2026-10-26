using Conduit.Runtime;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Correlates an outbox entry's MessageContext fields with its in-memory
/// <see cref="DispatchEnvelope"/>. Created by <see cref="OutboxBehavior{TRequest,TResult}"/>
/// and consumed by <see cref="OutboxCapture"/> during flush. <paramref name="Properties"/> is the
/// entry's persisted properties as delivery restores them (<see cref="OutboxProperties.Read"/>), so
/// the flush runs listeners under exactly the bag a relay redelivery would; null when there are none.
/// </summary>
internal sealed record WrittenOutboxMessage(
    string MessageId,
    string ConversationId,
    string? CausationId,
    DispatchEnvelope Envelope,
    IReadOnlyDictionary<string, object?>? Properties = null);
