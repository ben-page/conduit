namespace Conduit.Runtime;

/// <summary>
/// The correlation ids a redelivered message carries (<see cref="IConduitTransport.RedeliverAsync"/>): the
/// ones it first arrived with, so a dead-letter replay keeps the original's <see cref="MessageContext"/>
/// lineage instead of starting a conversation of its own.
/// </summary>
/// <param name="MessageId">The original message id.</param>
/// <param name="CausationId">The original causation id; null lets the receiver default it to the message id.</param>
/// <param name="ConversationId">The original conversation id; null lets the receiver default it to the message id.</param>
public sealed record RedeliveryIds(string MessageId, string? CausationId, string? ConversationId);
