using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// The <see cref="IJobs"/> implementation for the no-database deployment. Everything it can answer,
/// it answers from the in-memory registry; everything it changes, it changes on this host only.
/// </summary>
internal sealed class InMemoryJobs : IJobs
{
    private readonly ScheduleRegistry registry;
    private readonly InMemoryJobState state;
    private readonly IServiceScopeFactory scopeFactory;
    private readonly TimeProvider timeProvider;

    public InMemoryJobs(
        ScheduleRegistry registry,
        InMemoryJobState state,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider)
    {
        this.registry = registry;
        this.state = state;
        this.scopeFactory = scopeFactory;
        this.timeProvider = timeProvider;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobScheduleInfo>> GetSchedulesAsync(CancellationToken cancellationToken)
    {
        var now = this.timeProvider.GetUtcNow();
        IReadOnlyList<JobScheduleInfo> schedules =
        [
            .. this.registry.Schedules.Select(schedule => new JobScheduleInfo(
                schedule.Name,
                schedule.NextOccurrenceAfter(now),
                LastCompletedOccurrence: null,
                this.state.IsPaused(schedule.Name),
                PausedAt: null)
            {
                IsOnDemand = schedule.IsOnDemand,
            }),
        ];

        return Task.FromResult(schedules);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRunInfo>> GetInFlightAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<JobRunInfo>>([]);

    /// <inheritdoc/>
    public Task<IReadOnlyList<JobRunInfo>> GetFailedAsync(
        JobFilter filter, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<JobRunInfo>>([]);

    /// <inheritdoc/>
    /// <remarks>Always an empty page. There is no ledger, so there is nothing to have a history of.</remarks>
    public Task<JobHistoryPage> GetHistoryAsync(JobHistoryFilter filter, CancellationToken cancellationToken)
        => Task.FromResult(new JobHistoryPage { Entries = [], NextPageToken = null, TotalCount = 0 });

    /// <inheritdoc/>
    /// <remarks>
    /// Always empty. The in-memory scheduler dispatches straight through <see cref="IMediator"/> with
    /// no ledger for a run to be pending in, so this is truthful rather than a stub.
    /// </remarks>
    public Task<IReadOnlyList<JobRunInfo>> GetPendingAsync(
        JobFilter filter, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<JobRunInfo>>([]);

    /// <inheritdoc/>
    /// <remarks>
    /// Always empty. Event triggers require a persistence engine and are rejected at startup without
    /// one, so a host on this path has no scopes.
    /// </remarks>
    public Task<IReadOnlyList<JobScopeInfo>> GetPendingScopesAsync(
        JobFilter filter, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<JobScopeInfo>>([]);

    /// <inheritdoc/>
    public Task PauseAsync(string scheduleName, CancellationToken cancellationToken)
    {
        this.state.SetPaused(scheduleName, value: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ResumeAsync(string scheduleName, CancellationToken cancellationToken)
    {
        this.state.SetPaused(scheduleName, value: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Runs the command inline and returns when it finishes. The overlap rule is enforced against
    /// this host only — the scheduled run or another manual one in progress here — because without a
    /// database every host fires every schedule anyway; there is nothing shared to coordinate through.
    /// While it runs, <see cref="CancelAsync"/> with the schedule's name reaches it, as it does a
    /// scheduled run. A run of an <c>AllowOverlap()</c> schedule that starts beside another is not
    /// registered, so only the first of the two is reachable that way.
    /// </remarks>
    public async Task TriggerNowAsync(string scheduleName, CancellationToken cancellationToken)
    {
        var schedule = this.registry.Schedules
            .FirstOrDefault(candidate => string.Equals(candidate.Name, scheduleName, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"No job schedule named '{scheduleName}' is declared.", nameof(scheduleName));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tracked = this.state.TryTrack(schedule.Name, cancellation);
        if (!tracked && !schedule.AllowsOverlap)
        {
            throw new JobOverlapException(schedule.Name);
        }

        try
        {
            await using var scope = this.scopeFactory.CreateAsyncScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.ExecuteAsync(schedule.CreateCommand(), cancellation.Token);
        }
        finally
        {
            if (tracked)
            {
                this.state.Forget(schedule.Name, cancellation);
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// There is no ledger, so the only thing a key can name here is a schedule running on this host.
    /// Anything else is indistinguishable from a job that has already finished.
    /// </remarks>
    public Task<CancellationOutcome> CancelAsync(string jobKey, CancellationToken cancellationToken)
        => Task.FromResult(
            this.state.TryCancel(jobKey) ? CancellationOutcome.Requested : CancellationOutcome.NotFound);
}
