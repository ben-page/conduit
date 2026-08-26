using Conduit.Jobs.Persistence;

namespace Conduit.Jobs;

/// <summary>
/// Narrows a <see cref="IJobs.GetHistoryAsync"/> query. All fields are optional — a <see langword="null"/>
/// field is not applied. Mirrors <c>Conduit.Durability.DeadLetterQueue.DeadLetterFilter</c>.
/// </summary>
public sealed class JobHistoryFilter
{
    /// <summary>Restricts results to one schedule or trigger.</summary>
    public string? ScheduleName { get; init; }

    /// <summary>Restricts results to one run key.</summary>
    public string? JobKey { get; init; }

    /// <summary>Restricts results to one outcome.</summary>
    public JobRunOutcome? Outcome { get; init; }

    /// <summary>Only entries that reached their outcome at or after this time.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only entries that reached their outcome at or before this time.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Maximum number of entries per page. Default 25.</summary>
    public int PageSize { get; init; } = 25;

    /// <summary>Opaque cursor for pagination. Null for the first page.</summary>
    public string? PageToken { get; init; }
}
