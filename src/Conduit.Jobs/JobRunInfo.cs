using Conduit.Jobs.Persistence;

namespace Conduit.Jobs;

/// <summary>
/// One run in the ledger. The ledger holds work, not history — a successful run's row is deleted —
/// so this shows what is in flight or has failed. A run's terminal outcome, independent of whatever
/// later happens to this ledger row, is <see cref="JobRunHistoryInfo"/> via
/// <see cref="IJobs.GetHistoryAsync"/>.
/// </summary>
/// <param name="JobKey">The run's unique key.</param>
/// <param name="ScheduleName">The declaring schedule or trigger.</param>
/// <param name="MessageType">The command being dispatched.</param>
/// <param name="RunAt">When the run is (or was) due.</param>
/// <param name="Status">Where the run is in its lifecycle.</param>
/// <param name="AttemptCount">How many attempts have already failed.</param>
/// <param name="LastError">The most recent failure message.</param>
/// <param name="StartedAt">When the current attempt began.</param>
/// <param name="CancellationRequested">Whether an operator has asked for it to stop.</param>
public sealed record JobRunInfo(
    string JobKey,
    string? ScheduleName,
    string MessageType,
    DateTimeOffset RunAt,
    JobRunStatus Status,
    int AttemptCount,
    string? LastError,
    DateTimeOffset? StartedAt,
    bool CancellationRequested);
