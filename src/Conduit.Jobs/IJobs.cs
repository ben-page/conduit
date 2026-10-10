namespace Conduit.Jobs;

/// <summary>
/// The operational surface for background jobs: what is declared, what is running, what failed, and
/// the controls an operator needs.
/// </summary>
/// <remarks>
/// Without a persistence engine this degrades honestly rather than pretending: queries answer from
/// the in-memory registry, the run lists are empty because there is no ledger, and pause,
/// trigger-now and cancel affect only the host they were called on and are lost on restart.
/// </remarks>
public interface IJobs
{
    /// <summary>Lists every declared schedule with its next occurrence and pause state.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The declared schedules.</returns>
    public Task<IReadOnlyList<JobScheduleInfo>> GetSchedulesAsync(CancellationToken cancellationToken);

    /// <summary>Lists runs currently claimed by some host.</summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The in-flight runs.</returns>
    public Task<IReadOnlyList<JobRunInfo>> GetInFlightAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Runs that are waiting rather than running — including one between attempts, which appears in
    /// neither <see cref="GetInFlightAsync"/> nor <see cref="GetFailedAsync"/>. Soonest first.
    /// </summary>
    /// <param name="filter">Narrows the result to one schedule or trigger.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The waiting runs.</returns>
    public Task<IReadOnlyList<JobRunInfo>> GetPendingAsync(
        JobFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Coalesced work waiting to become a run, soonest first. For a projection this is queue depth:
    /// every key that changed while a run is in flight parks a scope until it finishes.
    /// </summary>
    /// <param name="filter">
    /// Narrows the result to one schedule or trigger. Only a trigger coalesces, so a schedule name
    /// matches nothing here.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The waiting scopes.</returns>
    public Task<IReadOnlyList<JobScopeInfo>> GetPendingScopesAsync(
        JobFilter filter, CancellationToken cancellationToken);

    /// <summary>Lists runs whose attempts are exhausted.</summary>
    /// <param name="filter">Narrows the result.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The failed runs.</returns>
    public Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(JobFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Browses the durable outcome of every run that has ever gone terminal — success included, not
    /// just failure. Unlike <see cref="GetFailedAsync"/>, an entry here is never superseded or cleared
    /// by later work for the same key, so it is what backs a dashboard rather than a live queue.
    /// </summary>
    /// <param name="filter">Narrows and pages the result.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The matching page of history entries.</returns>
    public Task<JobHistoryPage> GetHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Stops future occurrences of a schedule. An in-flight run is left alone — pause is about what
    /// happens next, not about stopping work already under way. The state lives in the database, so
    /// it survives a restart and applies cluster-wide.
    /// </summary>
    /// <param name="scheduleName">The schedule to pause.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task PauseAsync(string scheduleName, CancellationToken cancellationToken);

    /// <summary>Resumes a paused schedule from its next occurrence.</summary>
    /// <param name="scheduleName">The schedule to resume.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public Task ResumeAsync(string scheduleName, CancellationToken cancellationToken);

    /// <summary>
    /// Queues an extra run of a schedule now, without disturbing its cursor — the occurrence it was
    /// already waiting for still happens.
    /// </summary>
    /// <remarks>
    /// The schedule's overlap rule applies to a manual run as it does to an occurrence. Without
    /// <c>AllowOverlap()</c>, the call is refused while the schedule has a run outstanding — pending,
    /// awaiting a retry, or running, whether an occurrence or an earlier manual run — and the check
    /// and the insert are one atomic step against the scheduler on every host, so the two can never
    /// leave two runs outstanding between them. That holds while every host sharing the tables runs a
    /// Conduit version with this check, and every host declares the schedule's <c>AllowOverlap()</c>
    /// the same way: a host on an older version, or one that declares the schedule as allowing
    /// overlap, writes its runs without taking the lock. With <c>AllowOverlap()</c> it always
    /// queues. While a
    /// manual run is outstanding the scheduler skips the schedule's occurrences, exactly as it skips
    /// one behind an earlier occurrence. Without a persistence engine the run executes inline on this
    /// host, and "outstanding" means in progress on this host.
    /// </remarks>
    /// <param name="scheduleName">The schedule to run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <exception cref="ArgumentException">No schedule of that name is declared on this host.</exception>
    /// <exception cref="JobOverlapException">
    /// The schedule does not allow overlap and already has a run outstanding; nothing was queued.
    /// Server-side by design — catch it and answer your own refusal (an HTTP 409, say) if a client
    /// asked for the run.
    /// </exception>
    public Task TriggerNowAsync(string scheduleName, CancellationToken cancellationToken);

    /// <summary>
    /// Asks a run to stop. Returns an outcome rather than <see cref="Task"/> because the caller has
    /// to confront the ambiguity: <see cref="CancellationOutcome.Requested"/> means the running host
    /// will see the request within one heartbeat, not that the work stopped.
    /// </summary>
    /// <param name="jobKey">The run's key.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>What the request achieved.</returns>
    public Task<CancellationOutcome> CancelAsync(string jobKey, CancellationToken cancellationToken);
}
