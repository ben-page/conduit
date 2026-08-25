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
    /// Records success: deletes the run row and, for a schedule run, advances the cursor — in one
    /// transaction, so a deleted row can never be re-materialized by a host that has not yet seen
    /// the new cursor.
    /// </summary>
    /// <param name="id">The run's primary key.</param>
    /// <param name="scheduleName">The schedule to advance, or <see langword="null"/> for a trigger run.</param>
    /// <param name="occurrence">The occurrence to advance the cursor to.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task CompleteAsync(
        long id, string? scheduleName, DateTimeOffset? occurrence, CancellationToken cancellationToken);

    /// <summary>
    /// Releases a claim back to pending without recording an outcome and <b>without</b> touching the
    /// attempt count. Used on shutdown, where the drain is a handover rather than a failure, so
    /// another host resumes the work immediately instead of waiting out the claim timeout.
    /// </summary>
    /// <param name="id">The run's primary key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ReleaseAsync(long id, CancellationToken cancellationToken);

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

    /// <summary>Moves a run to a terminal status and releases its claim.</summary>
    /// <param name="id">The run's primary key.</param>
    /// <param name="status">The terminal status.</param>
    /// <param name="error">The failure message, when there is one.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task MarkTerminalAsync(
        long id, JobRunStatus status, string? error, CancellationToken cancellationToken);

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

    /// <summary>Deletes failed runs older than the retention cutoff.</summary>
    /// <param name="olderThan">The cutoff; runs due before it are deleted.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The number of rows deleted.</returns>
    public Task<int> PurgeFailedAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
}
