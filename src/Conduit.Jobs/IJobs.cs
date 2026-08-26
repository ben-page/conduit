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
    public Task<IReadOnlyList<JobRunInfo>> GetPendingAsync(JobFilter filter, CancellationToken cancellationToken);

    /// <summary>Lists runs whose attempts are exhausted.</summary>
    /// <param name="filter">Narrows the result.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The failed runs.</returns>
    public Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(JobFilter filter, CancellationToken cancellationToken);

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
    /// <param name="scheduleName">The schedule to run.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
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
