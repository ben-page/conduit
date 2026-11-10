namespace Conduit.Tests.Support;

/// <summary>
/// Inbound message for <see cref="FakeBrokerTransport"/>. Mirrors what a real broker message
/// carries: a type discriminator, a body, request/event classification, and free-form properties.
/// </summary>
public sealed class FakeBrokerMessage
{
    public required string Type { get; init; }

    public required BinaryData Body { get; init; }

    public bool IsRequest { get; init; }

    public Dictionary<string, string> Properties { get; init; } = [];

    public bool Acknowledged { get; set; }

    /// <summary>When set, every acknowledgement of this message throws it, as a closed channel would.</summary>
    public Exception? AcknowledgeFailure { get; init; }

    /// <summary>How many times the transport tried to acknowledge this message, failed attempts included.</summary>
    public int AcknowledgeAttempts { get; set; }

    public bool Abandoned { get; set; }
}
