namespace Conduit.DeadLetterQueue;

/// <summary>
/// Result of a single dead letter replay attempt.
/// </summary>
public sealed class ReplayResult
{
    /// <summary>The ID of the dead letter entry that was replayed.</summary>
    public required Guid DeadLetterId { get; init; }

    /// <summary>Whether the replay succeeded (message accepted by transport).</summary>
    public required bool Success { get; init; }

    /// <summary>Error details if replay failed. Null on success.</summary>
    public required string? ErrorMessage { get; init; }
}
