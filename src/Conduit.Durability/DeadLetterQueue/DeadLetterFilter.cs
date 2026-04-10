namespace Conduit.Durability.DeadLetterQueue;

/// <summary>
/// Filter criteria for querying the dead letter store.
/// All fields are optional — null fields are not applied.
/// </summary>
public sealed class DeadLetterFilter
{
    /// <summary>Filter by message type (exact match).</summary>
    public string? MessageType { get; init; }

    /// <summary>Filter by exception type (exact match).</summary>
    public string? ExceptionType { get; init; }

    /// <summary>Only entries dead-lettered at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only entries dead-lettered at or before this time.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Maximum number of entries per page. Default 25.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>Opaque cursor for pagination. Null for the first page.</summary>
    public string? PageToken { get; init; }
}
