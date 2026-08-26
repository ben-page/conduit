using System.Text.Json;
using Conduit.Jobs.Persistence;
using Conduit.Runtime;

namespace Conduit.Jobs.Runtime;

/// <summary>The <see cref="IJobs"/> implementation backed by the ledger.</summary>
internal sealed class DurableJobs : IJobs
{
    private readonly ScheduleRegistry registry;
    private readonly IJobStore store;
    private readonly TimeProvider timeProvider;

    public DurableJobs(ScheduleRegistry registry, IJobStore store, TimeProvider timeProvider)
    {
        this.registry = registry;
        this.store = store;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobScheduleInfo>> GetSchedulesAsync(CancellationToken cancellationToken)
    {
        var rows = await this.store.GetSchedulesAsync(cancellationToken);
        var byName = rows.ToDictionary(row => row.Name, StringComparer.Ordinal);
        var now = this.timeProvider.GetUtcNow();

        // Declarations come from code, so the list is the registry's, not the table's: a schedule
        // that was removed from the code should not keep appearing because its row is still there.
        return
        [
            .. this.registry.Schedules.Select(schedule =>
            {
                byName.TryGetValue(schedule.Name, out var row);
                return new JobScheduleInfo(
                    schedule.Name,
                    schedule.NextOccurrenceAfter(row?.LastCompletedOccurrence ?? now),
                    row?.LastCompletedOccurrence,
                    row?.IsPaused ?? false,
                    row?.PausedAt);
            }),
        ];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRunInfo>> GetInFlightAsync(CancellationToken cancellationToken)
        => [.. (await this.store.GetInFlightAsync(cancellationToken)).Select(ToInfo)];

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(
        JobFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var runs = await this.store.GetFailedAsync(filter.ScheduleName, cancellationToken);
        return [.. runs.Select(ToInfo)];
    }

    /// <inheritdoc/>
    public Task<JobHistoryPage> GetHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return this.store.QueryHistoryAsync(filter, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobRunInfo>> GetPendingAsync(
        JobFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var runs = await this.store.GetPendingAsync(filter.ScheduleName, cancellationToken);
        return [.. runs.Select(ToInfo)];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobScopeInfo>> GetPendingScopesAsync(
        JobFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var scopes = await this.store.GetScopesAsync(filter.ScheduleName, cancellationToken);
        return
        [
            .. scopes.Select(scope => new JobScopeInfo(
                scope.TriggerName,
                SplitCoalesceKey(scope.ScopeKey, scope.TriggerName),
                scope.MessageType,
                scope.DueAt,
                scope.Version)),
        ];
    }

    /// <inheritdoc/>
    public Task PauseAsync(string scheduleName, CancellationToken cancellationToken)
        => this.store.SetPausedAsync(
            scheduleName, paused: true, this.timeProvider.GetUtcNow(), cancellationToken);

    /// <inheritdoc/>
    public Task ResumeAsync(string scheduleName, CancellationToken cancellationToken)
        => this.store.SetPausedAsync(
            scheduleName, paused: false, this.timeProvider.GetUtcNow(), cancellationToken);

    /// <inheritdoc/>
    public async Task TriggerNowAsync(string scheduleName, CancellationToken cancellationToken)
    {
        var schedule = this.registry.Schedules
            .FirstOrDefault(candidate => string.Equals(candidate.Name, scheduleName, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"No job schedule named '{scheduleName}' is declared.", nameof(scheduleName));

        var now = this.timeProvider.GetUtcNow();
        var command = schedule.CreateCommand();

        // Occurrence is deliberately null: a manual run must not move the cursor, or triggering a
        // job by hand would swallow the occurrence the schedule was already waiting for.
        await this.store.TryMaterializeAsync(
            new JobRun
            {
                JobKey = $"{schedule.Name}@manual-{now.UtcDateTime:O}",
                ScheduleName = schedule.Name,
                Occurrence = null,
                MessageType = command.GetType().FullName!,
                Payload = JsonSerializer.Serialize(command, command.GetType(), ConduitRuntime.JsonOptions),
                RunAt = now,
                Status = JobRunStatus.Pending,
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<CancellationOutcome> CancelAsync(string jobKey, CancellationToken cancellationToken)
    {
        if (await this.store.RequestCancellationAsync(jobKey, cancellationToken))
        {
            return CancellationOutcome.Requested;
        }

        // The flag was refused, which means either there is no such run or it has already finished.
        // Collapsing those into one answer would let a caller read "nothing to cancel" as success.
        var run = await this.store.GetRunAsync(jobKey, cancellationToken);
        return run is null ? CancellationOutcome.NotFound : CancellationOutcome.AlreadyComplete;
    }

    /// <summary>
    /// Recovers the coalesce key from the composed scope key. The trigger name is a prefix followed by
    /// '@'; the key itself may contain '@', so the split is by prefix length, not by the separator.
    /// </summary>
    private static string SplitCoalesceKey(string scopeKey, string triggerName)
        => scopeKey.Length > triggerName.Length + 1
            ? scopeKey[(triggerName.Length + 1)..]
            : scopeKey;

    private static JobRunInfo ToInfo(JobRun run) => new(
        run.JobKey,
        run.ScheduleName,
        run.MessageType,
        run.RunAt,
        run.Status,
        run.AttemptCount,
        run.LastError,
        run.StartedAt,
        run.CancellationRequested);
}
