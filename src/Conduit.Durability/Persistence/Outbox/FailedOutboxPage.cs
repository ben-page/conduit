namespace Conduit.Durability.Persistence.Outbox;

/// <summary>A page of failed outbox entries, oldest first.</summary>
public sealed class FailedOutboxPage
{
    /// <summary>The entries in this page.</summary>
    public required IReadOnlyList<OutboxEntry> Entries { get; init; }

    /// <summary>Cursor for the next page. Null if this is the last page.</summary>
    public required string? NextPageToken { get; init; }

    /// <summary>Total count of matching entries. Null if the store cannot provide this efficiently.</summary>
    public required int? TotalCount { get; init; }
}
