using Conduit.Jobs;
using Conduit.Jobs.Persistence;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// An <see cref="IJobStore"/> that forwards every member to a real store except
/// <see cref="TryMergeScopeAsync"/>, which always reports that someone else got there first. That is
/// the merge contention <c>ScopeCoalescer</c> retries a bounded number of times, held permanently —
/// the hot-key-across-replicas case, driven straight to exhaustion.
/// </summary>
public sealed class ContendedJobStore : IJobStore
{
    private readonly IJobStore inner;

    /// <summary>Wraps a real store.</summary>
    /// <param name="inner">The store every member but the scope merge forwards to.</param>
    public ContendedJobStore(IJobStore inner) => this.inner = inner;

    /// <summary>
    /// Always refuses, whatever the caller read. The real store's compare-and-set returns
    /// <see langword="false"/> when another host merged first; this one never stops saying so.
    /// </summary>
    /// <param name="scopeKey">The scope key.</param>
    /// <param name="scopeId">The generation identity the caller read.</param>
    /// <param name="payload">The merged payload, discarded here.</param>
    /// <param name="version">The version the caller read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Always <see langword="false"/>.</returns>
    public Task<bool> TryMergeScopeAsync(
        string scopeKey, string scopeId, string payload, int version, CancellationToken cancellationToken)
        => Task.FromResult(false);

    /// <inheritdoc/>
    public Task EnsureTablesExistAsync(CancellationToken cancellationToken)
        => this.inner.EnsureTablesExistAsync(cancellationToken);

    /// <inheritdoc/>
    public Task ReconcileSchedulesAsync(
        IReadOnlyList<string> scheduleNames, CancellationToken cancellationToken)
        => this.inner.ReconcileSchedulesAsync(scheduleNames, cancellationToken);

    /// <inheritdoc/>
    public Task<JobScheduleRow?> GetScheduleAsync(string name, CancellationToken cancellationToken)
        => this.inner.GetScheduleAsync(name, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobScheduleRow>> GetSchedulesAsync(CancellationToken cancellationToken)
        => this.inner.GetSchedulesAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<bool> TryMaterializeAsync(JobRun run, CancellationToken cancellationToken)
        => this.inner.TryMaterializeAsync(run, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRun>> ClaimDueAsync(
        string claimToken, DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        => this.inner.ClaimDueAsync(claimToken, now, batchSize, cancellationToken);

    /// <inheritdoc/>
    public Task<JobRun?> InsertClaimedAsync(
        JobRun run, string claimToken, DateTimeOffset now, CancellationToken cancellationToken)
        => this.inner.InsertClaimedAsync(run, claimToken, now, cancellationToken);

    /// <inheritdoc/>
    public Task<HeartbeatResult> HeartbeatAsync(
        long id, string claimToken, DateTimeOffset now, CancellationToken cancellationToken)
        => this.inner.HeartbeatAsync(id, claimToken, now, cancellationToken);

    /// <inheritdoc/>
    public Task CompleteAsync(
        JobRun run, string? scheduleName, DateTimeOffset? occurrence, DateTimeOffset now,
        CancellationToken cancellationToken)
        => this.inner.CompleteAsync(run, scheduleName, occurrence, now, cancellationToken);

    /// <inheritdoc/>
    public Task ReleaseAsync(long id, string claimToken, CancellationToken cancellationToken)
        => this.inner.ReleaseAsync(id, claimToken, cancellationToken);

    /// <inheritdoc/>
    public Task<int> IncrementAttemptAsync(
        long id, DateTimeOffset nextRunAt, string error, CancellationToken cancellationToken)
        => this.inner.IncrementAttemptAsync(id, nextRunAt, error, cancellationToken);

    /// <inheritdoc/>
    public Task MarkTerminalAsync(
        JobRun run,
        JobRunStatus status,
        string? error,
        string? scheduleName,
        DateTimeOffset? occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => this.inner.MarkTerminalAsync(run, status, error, scheduleName, occurrence, now, cancellationToken);

    /// <inheritdoc/>
    public Task AdvanceCursorAsync(
        string scheduleName, DateTimeOffset occurrence, CancellationToken cancellationToken)
        => this.inner.AdvanceCursorAsync(scheduleName, occurrence, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> HasOutstandingAsync(string scheduleName, CancellationToken cancellationToken)
        => this.inner.HasOutstandingAsync(scheduleName, cancellationToken);

    /// <inheritdoc/>
    public Task<DateTimeOffset?> GetNextPendingDueAsync(CancellationToken cancellationToken)
        => this.inner.GetNextPendingDueAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<JobRun?> GetRunAsync(string jobKey, CancellationToken cancellationToken)
        => this.inner.GetRunAsync(jobKey, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRun>> GetInFlightAsync(CancellationToken cancellationToken)
        => this.inner.GetInFlightAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRun>> GetFailedAsync(
        string? scheduleName, CancellationToken cancellationToken)
        => this.inner.GetFailedAsync(scheduleName, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRun>> GetPendingAsync(
        string? scheduleName, CancellationToken cancellationToken)
        => this.inner.GetPendingAsync(scheduleName, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> RequestCancellationAsync(string jobKey, CancellationToken cancellationToken)
        => this.inner.RequestCancellationAsync(jobKey, cancellationToken);

    /// <inheritdoc/>
    public Task SetPausedAsync(
        string name, bool paused, DateTimeOffset now, CancellationToken cancellationToken)
        => this.inner.SetPausedAsync(name, paused, now, cancellationToken);

    /// <inheritdoc/>
    public Task<int> PurgeTerminalAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => this.inner.PurgeTerminalAsync(olderThan, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> TryClearTerminalRunAsync(string jobKey, CancellationToken cancellationToken)
        => this.inner.TryClearTerminalRunAsync(jobKey, cancellationToken);

    /// <inheritdoc/>
    public Task<JobHistoryPage> QueryHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken)
        => this.inner.QueryHistoryAsync(filter, cancellationToken);

    /// <inheritdoc/>
    public Task<int> PurgeHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken)
        => this.inner.PurgeHistoryAsync(olderThan, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> TryInsertScopeAsync(JobScope scope, CancellationToken cancellationToken)
        => this.inner.TryInsertScopeAsync(scope, cancellationToken);

    /// <inheritdoc/>
    public Task<JobScope?> GetScopeAsync(string scopeKey, CancellationToken cancellationToken)
        => this.inner.GetScopeAsync(scopeKey, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> TryDeleteScopeAsync(
        string scopeKey, string scopeId, int version, CancellationToken cancellationToken)
        => this.inner.TryDeleteScopeAsync(scopeKey, scopeId, version, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobScope>> GetDueScopesAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken)
        => this.inner.GetDueScopesAsync(now, batchSize, cancellationToken);

    /// <inheritdoc/>
    public Task<DateTimeOffset?> GetNextScopeDueAsync(CancellationToken cancellationToken)
        => this.inner.GetNextScopeDueAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobScope>> GetScopesAsync(
        string? triggerName, CancellationToken cancellationToken)
        => this.inner.GetScopesAsync(triggerName, cancellationToken);
}
