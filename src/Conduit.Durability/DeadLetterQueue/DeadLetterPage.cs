namespace Conduit.Durability.DeadLetterQueue;

/// <summary>
/// A page of dead letter entries returned by <see cref="IDeadLetterStore.QueryAsync"/>.
/// </summary>
public sealed class DeadLetterPage
{
    /// <summary>The entries in this page.</summary>
    public required IReadOnlyList<DeadLetterEntry> Entries { get; init; }

    /// <summary>Cursor for the next page. Null if this is the last page.</summary>
    public required string? NextPageToken { get; init; }

    /// <summary>
    /// Total count of matching entries. Null if the store cannot provide this efficiently.
    /// </summary>
    public required int? TotalCount { get; init; }
}
