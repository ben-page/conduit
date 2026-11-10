namespace Conduit.Durability.Persistence.Outbox;

/// <summary>
/// Filter and page for listing the outbox entries whose retries ran out (<see cref="OutboxEntryStatus.Failed"/>).
/// </summary>
public sealed class FailedOutboxFilter
{
    /// <summary>Only entries of this message type (exact match on the full type name). Null for every type.</summary>
    public string? MessageType { get; init; }

    /// <summary>Maximum number of entries per page. Default 25.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>The <see cref="FailedOutboxPage.NextPageToken"/> of the previous page. Null for the first page.</summary>
    public string? PageToken { get; init; }
}
