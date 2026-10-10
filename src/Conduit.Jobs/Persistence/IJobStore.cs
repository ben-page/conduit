namespace Conduit.Jobs.Persistence;

/// <summary>
/// All jobs SQL. Scoped, because the engine's <see cref="Conduit.Persistence.IConduitConnectionFactory"/>
/// is scoped — the background services resolve it from a fresh scope per operation.
/// </summary>
public interface IJobStore
{
    /// <summary>Creates both jobs tables if they do not already exist.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task EnsureTablesExistAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a cursor row for every declared schedule that does not have one yet.</summary>
    /// <param name="scheduleNames">The declared schedule names.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ReconcileSchedulesAsync(IReadOnlyList<string> scheduleNames, CancellationToken cancellationToken);

    /// <summary>Reads one schedule's cursor and pause state.</summary>
    /// <param name="name">The schedule name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The row, or <see langword="null"/> when the schedule has none yet.</returns>
    public Task<JobScheduleRow?> GetScheduleAsync(string name, CancellationToken cancellationToken);

    /// <summary>Reads every schedule's cursor and pause state.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Every stored schedule row.</returns>
    public Task<IReadOnlyList<JobScheduleRow>> GetSchedulesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a run row unless one with the same <see cref="JobRun.JobKey"/> already exists. N
    /// replicas racing to materialize one occurrence produce exactly one row.
    /// </summary>
    /// <param name="run">The run to materialize.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when this caller wrote the row.</returns>
    public Task<bool> TryMaterializeAsync(JobRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a run of a schedule that does not allow overlap, unless the schedule already has an
    /// outstanding run under a different key — the check and the insert atomic with respect to every
    /// other call of this method for the same schedule, on any host. Both writers of such a schedule's
    /// runs — the scheduler materializing an occurrence and a manual trigger — go through here, so no
    /// interleaving of them leaves two runs outstanding. A row with the run's own key does not count as
    /// overlap: that is the same run, which every replica materializes, and it collapses as in
    /// <see cref="TryMaterializeAsync"/>. A run with an <see cref="JobRun.Occurrence"/> is also checked
    /// against the schedule's cursor under the same lock, so an occurrence another host has already
    /// completed — its row deleted, the cursor advanced — is never written again.
    /// </summary>
    /// <remarks>
    /// The protocol is one transaction that first takes the schedule's <c>conduit_job_schedules</c> row
    /// lock with a no-op <c>UPDATE</c>, then counts outstanding runs and inserts. A run of the schedule
    /// written any other way (by <see cref="TryMaterializeAsync"/>, or by a host that predates this
    /// method) does not take the lock, and is outside the guarantee.
    /// </remarks>
    /// <param name="run">The run to materialize; its <see cref="JobRun.ScheduleName"/> names the schedule.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the run was written, already existed, or was refused as overlapping.</returns>
    public Task<MaterializeOutcome> TryMaterializeWithoutOverlapAsync(JobRun run, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims due runs for this worker and reads back exactly what it claimed. A run is
    /// due when its <c>RunAt</c> has arrived and it is either pending or holds a claim that has gone
    /// stale — the latter being how a crashed host's work is recovered.
    /// </summary>
    /// <param name="claimToken">
    /// A token identifying this claim. It <b>must be unique per call</b>: claimed rows are read back
    /// by token, so reusing one while an earlier claim is still in flight would return that earlier
    /// claim's rows as well.
    /// </param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="batchSize">The maximum number of runs to claim.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The runs this worker now owns.</returns>
    public Task<IReadOnlyList<JobRun>> ClaimDueAsync(
        string claimToken, DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a run already claimed by this host, for an immediate event trigger: the host that
    /// received the event is the host that will run it, so a separate claim round-trip would be
    /// wasted. Ignored when a row with the same key already exists, which is what makes a redelivered
    /// event run once.
    /// </summary>
    /// <param name="run">The run to insert.</param>
    /// <param name="claimToken">This host's claim token.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The inserted run when this caller wrote it, otherwise <see langword="null"/>.</returns>
    public Task<JobRun?> InsertClaimedAsync(
        JobRun run, string claimToken, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Renews a claim and reads the cancellation flag back in the same round-trip.</summary>
    /// <param name="id">The run's primary key.</param>
    /// <param name="claimToken">The token this host claimed under.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>Whether the claim is still held, and whether cancellation was requested.</returns>
    public Task<HeartbeatResult> HeartbeatAsync(
        long id, string claimToken, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Records success: writes a <see cref="JobRunOutcome.Completed"/> history row, deletes the run
    /// row (or keeps it as <see cref="JobRunStatus.Completed"/>), and — for a schedule run — advances
    /// the cursor, all in one transaction. A deleted row can never be re-materialized by a host that
    /// has not yet seen the new cursor, and the history row cannot be written without the ledger
    /// transition landing alongside it.
    /// </summary>
    /// <param name="run">The completed run, read before this call.</param>
    /// <param name="scheduleName">The schedule to advance, or <see langword="null"/> for a trigger run.</param>
    /// <param name="occurrence">The occurrence to advance the cursor to.</param>
    /// <param name="retainRunKey">
    /// Keep the row as <see cref="JobRunStatus.Completed"/> instead of deleting it, so its run key
    /// keeps refusing inserts until <see cref="PurgeCompletedAsync"/> removes it. Set for a
    /// non-coalescing trigger run, whose key is the event's message id and has no cursor behind it.
    /// </param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task CompleteAsync(
        JobRun run, string? scheduleName, DateTimeOffset? occurrence, bool retainRunKey, DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>
    /// Releases a claim back to pending without recording an outcome and <b>without</b> touching the
    /// attempt count. Used on shutdown, where the drain is a handover rather than a failure, so
    /// another host resumes the work immediately instead of waiting out the claim timeout.
    /// </summary>
    /// <param name="id">The run's primary key.</param>
    /// <param name="claimToken">
    /// The token this host claimed under. The release is conditional on it, so a claim taken by
    /// another host in the meantime is never handed back out from under them.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ReleaseAsync(long id, string claimToken, CancellationToken cancellationToken);

    /// <summary>
    /// Records a failed attempt: increments the attempt count, reschedules to the backoff time and
    /// releases the claim, so the retry survives a process restart.
    /// </summary>
    /// <param name="id">The run's primary key.</param>
    /// <param name="nextRunAt">When the next attempt becomes due.</param>
    /// <param name="error">The failure message.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The new attempt count.</returns>
    public Task<int> IncrementAttemptAsync(
        long id, DateTimeOffset nextRunAt, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a matching history row, moves a run to a terminal status, releases its claim and, for a
    /// schedule run, advances the cursor past the occurrence — all in one transaction. The cursor
    /// advance and the ledger transition are needed for the reasons <see cref="CompleteAsync"/>
    /// documents; the history row is needed so the outcome survives whatever later happens to the
    /// ledger row (a coalescing pickup superseding it, or the retention purge).
    /// </summary>
    /// <param name="run">The run reaching this terminal status, read before this call.</param>
    /// <param name="status">The terminal status.</param>
    /// <param name="error">The failure message, when there is one.</param>
    /// <param name="scheduleName">The schedule to advance, or <see langword="null"/> for a trigger run.</param>
    /// <param name="occurrence">The occurrence to advance the cursor to.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task MarkTerminalAsync(
        JobRun run,
        JobRunStatus status,
        string? error,
        string? scheduleName,
        DateTimeOffset? occurrence,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Advances a schedule's cursor past an occurrence that will not be run again.</summary>
    /// <param name="scheduleName">The schedule to advance.</param>
    /// <param name="occurrence">The occurrence to advance to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task AdvanceCursorAsync(
        string scheduleName, DateTimeOffset occurrence, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the schedule has a run that has not yet succeeded — running, or awaiting a retry. A
    /// retry-pending run counts, because the previous occurrence has not succeeded and starting a
    /// second is exactly the overlap the policy exists to prevent.
    /// </summary>
    /// <param name="scheduleName">The schedule to check.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when a non-terminal run exists.</returns>
    public Task<bool> HasOutstandingAsync(string scheduleName, CancellationToken cancellationToken);

    /// <summary>
    /// When the earliest not-yet-claimed run becomes due. The sweep waits until then instead of
    /// polling, so steady-state traffic tracks the work outstanding rather than the passage of time.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The earliest pending due time, or <see langword="null"/> when nothing is pending.</returns>
    public Task<DateTimeOffset?> GetNextPendingDueAsync(CancellationToken cancellationToken);

    /// <summary>Reads one run by its key.</summary>
    /// <param name="jobKey">The run key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The run, or <see langword="null"/> when no row has that key.</returns>
    public Task<JobRun?> GetRunAsync(string jobKey, CancellationToken cancellationToken);

    /// <summary>Reads every run currently claimed by some host.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The in-flight runs.</returns>
    public Task<IReadOnlyList<JobRun>> GetInFlightAsync(CancellationToken cancellationToken);

    /// <summary>Reads runs whose attempts are exhausted, newest first.</summary>
    /// <param name="scheduleName">Restricts the result to one schedule when supplied.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The failed runs.</returns>
    public Task<IReadOnlyList<JobRun>> GetFailedAsync(
        string? scheduleName, CancellationToken cancellationToken);

    /// <summary>
    /// Reads runs that are waiting rather than running: a delayed trigger not yet due, an occurrence
    /// materialized but unclaimed, and — the case that matters most — a run between attempts, whose
    /// <c>RunAt</c> is when the next attempt falls due. Soonest first.
    /// </summary>
    /// <param name="scheduleName">Restricts the result to one schedule or trigger when supplied.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The waiting runs.</returns>
    public Task<IReadOnlyList<JobRun>> GetPendingAsync(
        string? scheduleName, CancellationToken cancellationToken);

    /// <summary>Flags a run for cancellation, to be observed by the running host's next heartbeat.</summary>
    /// <param name="jobKey">The run key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when the flag was set on a non-terminal run.</returns>
    public Task<bool> RequestCancellationAsync(string jobKey, CancellationToken cancellationToken);

    /// <summary>Sets a schedule's pause flag. Cluster-wide, because the state is in the database.</summary>
    /// <param name="name">The schedule name.</param>
    /// <param name="paused">Whether future occurrences are suppressed.</param>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task SetPausedAsync(string name, bool paused, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes terminal runs older than the retention cutoff. Cancelled rows are purged alongside
    /// failed ones: both are kept only so an operator can see what happened, and neither is ever
    /// retried, so leaving cancelled rows behind would grow the table without bound.
    /// </summary>
    /// <param name="olderThan">The cutoff; runs due before it are deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public Task<int> PurgeTerminalAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes <see cref="JobRunStatus.Completed"/> runs older than the retention cutoff, which ends
    /// their redelivery window. Separate from <see cref="PurgeTerminalAsync"/> because the two
    /// retentions answer different questions — how long an operator can inspect a failure, and how
    /// late a broker may redeliver an event.
    /// </summary>
    /// <param name="olderThan">The cutoff; runs due before it are deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public Task<int> PurgeCompletedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a terminal run holding this key, so newer work can take it. A coalescing trigger's run
    /// key is its coalesce key, so a failed or cancelled row would otherwise hold that key for the
    /// whole retention window and every later scope for it would silently never run. The attempts that
    /// were exhausted belonged to that unit of work; the scope waiting behind it is a different one.
    /// </summary>
    /// <param name="jobKey">The run key to free.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when a terminal row was removed.</returns>
    public Task<bool> TryClearTerminalRunAsync(string jobKey, CancellationToken cancellationToken);

    /// <summary>
    /// Browses run history with optional filtering and pagination. Unlike the ledger queries above,
    /// history is never superseded or cleared — every terminal outcome a run ever reached stays
    /// queryable here until <see cref="PurgeHistoryAsync"/> removes it.
    /// </summary>
    /// <param name="filter">Narrows and pages the result.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The matching page of history entries.</returns>
    public Task<JobHistoryPage> QueryHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken);

    /// <summary>Deletes history entries older than the retention cutoff.</summary>
    /// <param name="olderThan">The cutoff; entries completed before it are deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public Task<int> PurgeHistoryAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a scope unless one with the same key already exists. Two hosts racing to open a scope for
    /// one coalesce key produce exactly one row, exactly as occurrence materialization does.
    /// </summary>
    /// <param name="scope">The scope to open.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when this caller wrote the row.</returns>
    public Task<bool> TryInsertScopeAsync(JobScope scope, CancellationToken cancellationToken);

    /// <summary>Reads one scope by its key.</summary>
    /// <param name="scopeKey">The scope key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The scope, or <see langword="null"/> when no row has that key.</returns>
    public Task<JobScope?> GetScopeAsync(string scopeKey, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a scope's payload and bumps its version, conditional on the generation and version the
    /// caller read. <see langword="false"/> means another host merged first, or the scope was picked
    /// up and a fresh generation opened under the same key, and the caller must re-read rather than
    /// retry blindly — a lost merge narrows the scope and loses work. The guard is on the pair — the
    /// generation (<paramref name="scopeId"/>) and the version within it — because <c>Version</c>
    /// alone resets to 0 on every new generation and cannot by itself tell one generation from the
    /// next. <c>DueAt</c> is deliberately untouched: merging is leading-edge.
    /// </summary>
    /// <param name="scopeKey">The scope key.</param>
    /// <param name="scopeId">The generation identity the caller read.</param>
    /// <param name="payload">The merged, reserialized command.</param>
    /// <param name="version">The version the caller read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when the merge was applied.</returns>
    public Task<bool> TryMergeScopeAsync(
        string scopeKey, string scopeId, string payload, int version, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a scope, conditional on the generation and version pickup materialized from. A merge
    /// that landed since, or a fresh generation opened after a prior one was already picked up, leaves
    /// the row alive under a guard this call will not match, so the newer scope becomes its own run
    /// rather than being discarded. The guard is on the pair — the generation
    /// (<paramref name="scopeId"/>) and the version within it — because <c>Version</c> alone resets to
    /// 0 on every new generation and cannot by itself tell one generation from the next.
    /// </summary>
    /// <param name="scopeKey">The scope key.</param>
    /// <param name="scopeId">The generation identity pickup read.</param>
    /// <param name="version">The version pickup read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><see langword="true"/> when the row was deleted.</returns>
    public Task<bool> TryDeleteScopeAsync(
        string scopeKey, string scopeId, int version, CancellationToken cancellationToken);

    /// <summary>Reads scopes whose due time has arrived, soonest first.</summary>
    /// <param name="now">The current instant, from the injected <see cref="TimeProvider"/>.</param>
    /// <param name="batchSize">The maximum number of scopes to read.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The due scopes.</returns>
    public Task<IReadOnlyList<JobScope>> GetDueScopesAsync(
        DateTimeOffset now, int batchSize, CancellationToken cancellationToken);

    /// <summary>
    /// When the earliest scope becomes due. The sweep takes the minimum of this and the earliest
    /// pending run, or a delayed coalescing scope would run up to a claim timeout late.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The earliest scope due time, or <see langword="null"/> when there are none.</returns>
    public Task<DateTimeOffset?> GetNextScopeDueAsync(CancellationToken cancellationToken);

    /// <summary>Reads every waiting scope, soonest first.</summary>
    /// <param name="triggerName">Restricts the result to one trigger when supplied.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The waiting scopes.</returns>
    public Task<IReadOnlyList<JobScope>> GetScopesAsync(
        string? triggerName, CancellationToken cancellationToken);
}
