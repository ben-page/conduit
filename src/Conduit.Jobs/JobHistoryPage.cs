namespace Conduit.Jobs;

/// <summary>
/// A page of run history entries returned by <see cref="IJobs.GetHistoryAsync"/>. Mirrors
/// <c>Conduit.Durability.DeadLetterQueue.DeadLetterPage</c>.
/// </summary>
public sealed class JobHistoryPage
{
    /// <summary>The entries in this page, newest outcome first.</summary>
    public required IReadOnlyList<JobRunHistoryInfo> Entries { get; init; }

    /// <summary>Cursor for the next page. Null if this is the last page.</summary>
    public required string? NextPageToken { get; init; }

    /// <summary>Total count of matching entries. Null if the store cannot provide this efficiently.</summary>
    public required int? TotalCount { get; init; }
}
