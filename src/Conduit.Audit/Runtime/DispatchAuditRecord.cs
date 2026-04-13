using System.Text.Json;
using Conduit.Runtime;

namespace Conduit.Audit.Runtime;

/// <summary>
/// A consolidated audit record for a single dispatch operation.
/// Contains the message context, payload, and all observations that occurred during dispatch.
/// </summary>
public sealed class DispatchAuditRecord
{
    /// <summary>The unique identifier for this dispatch.</summary>
    public required string MessageId { get; init; }

    /// <summary>The conversation identifier shared across all messages in a causal chain.</summary>
    public required string ConversationId { get; init; }

    /// <summary>The message that caused this dispatch, if any.</summary>
    public string? CausationId { get; init; }

    /// <summary>The full type name of the dispatched message.</summary>
    public required string MessageType { get; init; }

    /// <summary>The kind of message: Command, Query, or Event.</summary>
    public required string MessageKind { get; init; }

    /// <summary>The name of the service that processed the dispatch.</summary>
    public required string ServiceName { get; init; }

    /// <summary>The raw JSON payload of the dispatched message, if captured.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>User-defined properties attached to the dispatch via <see cref="DispatchOptions"/>.</summary>
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }

    /// <summary>When the dispatch started.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>All observations recorded during this dispatch, in chronological order.</summary>
    public required IReadOnlyList<MessageObservation> Observations { get; init; }
}
