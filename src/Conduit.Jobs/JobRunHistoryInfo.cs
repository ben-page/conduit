using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;

namespace Conduit.Jobs;

/// <summary>
/// One durable record of a run's terminal outcome. Unlike <see cref="JobRunInfo"/> — which reflects
/// the live ledger and disappears once a run is superseded or purged — a history row is written at
/// the moment a run goes terminal and outlives whatever happens to the ledger row afterward.
/// </summary>
/// <param name="JobKey">The run's key at the time it went terminal.</param>
/// <param name="ScheduleName">The declaring schedule or trigger.</param>
/// <param name="MessageType">The command that was dispatched.</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="RunAt">When the run was due.</param>
/// <param name="StartedAt">When the final attempt began.</param>
/// <param name="CompletedAt">When the run reached this terminal outcome.</param>
/// <param name="AttemptCount">
/// How many attempts had already failed when its final attempt began, as <see cref="JobRunInfo.AttemptCount"/> read
/// then: 0 for a first-try success, and one less than the attempts it took whenever a final attempt ran.
/// </param>
/// <param name="LastError">The failure message, when the outcome is <see cref="JobRunOutcome.Failed"/>.</param>
public sealed record JobRunHistoryInfo(
    string JobKey,
    string? ScheduleName,
    string MessageType,
    JobRunOutcome Outcome,
    DateTimeOffset RunAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset CompletedAt,
    int AttemptCount,
    string? LastError)
{
    /// <summary>
    /// Whether <see cref="IJobs.TriggerNowAsync"/> queued this run, read from its key
    /// (<c>{schedule}@manual-{instant}</c>); an occurrence, a start-up run and a trigger's run are not manual.
    /// </summary>
    public bool IsManual => ManualRunKey.Is(this.JobKey, this.ScheduleName);
}
