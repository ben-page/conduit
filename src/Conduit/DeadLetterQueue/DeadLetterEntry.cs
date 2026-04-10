namespace Conduit.DeadLetterQueue;

/// <summary>
/// A message that failed during inbound handling and was captured for later replay or inspection.
/// </summary>
public sealed class DeadLetterEntry
{
    /// <summary>Unique identifier for this dead letter entry.</summary>
    public required Guid Id { get; init; }

    /// <summary>Full type name of the message (e.g., "Orders.Commands.CreateOrder").</summary>
    public required string MessageType { get; init; }

    /// <summary>Serialized message body.</summary>
    public required BinaryData Body { get; init; }

    /// <summary>Name of the endpoint that delivered this message.</summary>
    public required string SourceEndpoint { get; init; }

    /// <summary>Full type name of the exception that caused the failure.</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Exception message text.</summary>
    public string? ExceptionMessage { get; init; }

    /// <summary>Original MessageContext.MessageId.</summary>
    public string? MessageId { get; init; }

    /// <summary>Original MessageContext.CausationId.</summary>
    public string? CausationId { get; init; }

    /// <summary>Original MessageContext.ConversationId.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Number of times this message has been dead-lettered (including this time).</summary>
    public int AttemptCount { get; init; } = 1;

    /// <summary>When the message was captured into the dead letter store.</summary>
    public required DateTimeOffset DeadLetteredAt { get; init; }
}
