using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Renews one in-flight run's claim for as long as its host is genuinely running it.
/// </summary>
/// <remarks>
/// <para>
/// Without renewal, "this job is taking a long time" and "the host running it died" are the same
/// observation, and the recovery that makes the second case safe executes the first case twice. A
/// live host therefore keeps pushing its claim forward, so only a dead host's row ever goes stale.
/// </para>
/// <para>
/// Two more things ride the same round-trip, because it is already happening. The renewal reports
/// whether the claim is still held, so a host that lost it stops rather than finishing work it no
/// longer owns. And it reads back the cancellation flag, so an operator's request reaches the
/// running host within one interval without a second query, a second connection, or a poll loop.
/// <c>MaxRuntime</c> is enforced here too, since this loop already knows when the attempt began.
/// </para>
/// <para>
/// A host that cannot renew at all (its database unreachable, its connection hung) cannot learn that
/// the claim was taken, so it fences itself: once <c>ClaimTimeout</c> has passed since the last
/// renewal that landed — the moment another host may take the claim over — it gives the run up as
/// lost and cancels the job, rather than run on beside the host that took it.
/// </para>
/// <para>
/// Stopping the heartbeat (the job has ended) abandons a renewal in flight and never fences: the job's
/// outcome is written under its claim, and that guarded write decides whether the claim still held.
/// </para>
/// </remarks>
internal sealed partial class ClaimHeartbeat : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private int cancellationRequested;
    private int claimLost;

    private ClaimHeartbeat(
        JobRun run,
        TimeSpan? maxRuntime,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        JobsOptions options,
        CancellationTokenSource jobCancellation,
        ILogger logger)
    {
        this.loop = this.RunAsync(
            run, maxRuntime, scopeFactory, timeProvider, options, jobCancellation, logger);
    }

    /// <summary>Whether an operator asked for this run to stop, or it outran its <c>MaxRuntime</c>.</summary>
    public bool CancellationRequested => Volatile.Read(ref this.cancellationRequested) != 0;

    /// <summary>
    /// Whether another host has taken the claim, or this host could not renew it for a whole
    /// <c>ClaimTimeout</c>, after which another host may have. Either way this host owns nothing.
    /// </summary>
    public bool ClaimLost => Volatile.Read(ref this.claimLost) != 0;

    /// <summary>Starts renewing the claim until the returned heartbeat is disposed.</summary>
    public static ClaimHeartbeat Start(
        JobRun run,
        TimeSpan? maxRuntime,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        IOptions<JobsOptions> options,
        CancellationTokenSource jobCancellation,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new ClaimHeartbeat(
            run, maxRuntime, scopeFactory, timeProvider, options.Value, jobCancellation, logger);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await this.stop.CancelAsync();
        await this.loop;
        this.stop.Dispose();
    }

    private async Task RunAsync(
        JobRun run,
        TimeSpan? maxRuntime,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        JobsOptions options,
        CancellationTokenSource jobCancellation,
        ILogger logger)
    {
        var startedAt = run.StartedAt ?? timeProvider.GetUtcNow();

        // Another host may take the claim once ClaimedAt is ClaimTimeout old, and ClaimedAt is what the
        // last renewal that landed wrote: the claim's own instant until the first one does.
        var renewedAt = run.ClaimedAt ?? startedAt;
        var lastRenewalFailed = false;

        while (!this.stop.IsCancellationRequested)
        {
            var fenceAt = renewedAt + options.ClaimTimeout;
            try
            {
                await Task.Delay(WaitBefore(fenceAt), timeProvider, this.stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();
            if (lastRenewalFailed && now >= fenceAt)
            {
                // A renewal failed and none has landed since, and another host may now take the claim.
                // Another try here would have no deadline left to bound it, and could take as long to
                // fail as the last one did, so the fence lands at ClaimTimeout and not a timeout later.
                await this.FenceAsync(run, options, jobCancellation, logger);
                return;
            }

            HeartbeatResult beat;
            try
            {
                // A renewal still out at the fence counts as failed, so a hung connection cannot keep
                // the job running past the moment another host may take the claim. One begun after the
                // fence with the last renewal landed (this host woke late: a GC or VM pause) has no
                // deadline: if it lands, the claim was still this host's. Stopping cancels either.
                using var deadline = fenceAt > now
                    ? new CancellationTokenSource(fenceAt - now, timeProvider)
                    : new CancellationTokenSource();
                using var renewal = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, this.stop.Token);
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
                beat = await store.HeartbeatAsync(run.Id, run.ClaimToken!, now, renewal.Token);
            }
#pragma warning disable CA1031 // A missed beat is survivable until the claim could have gone stale.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                if (this.stop.IsCancellationRequested)
                {
                    // The job has ended; its outcome's write, guarded by the claim, decides from here.
                    return;
                }

                LogHeartbeatFailed(logger, run.JobKey, exception);
                if (timeProvider.GetUtcNow() < fenceAt)
                {
                    lastRenewalFailed = true;
                    continue;
                }

                await this.FenceAsync(run, options, jobCancellation, logger);
                return;
            }

            if (!beat.ClaimHeld)
            {
                LogClaimLost(logger, run.JobKey);
                Volatile.Write(ref this.claimLost, 1);
                await jobCancellation.CancelAsync();
                return;
            }

            renewedAt = now;
            lastRenewalFailed = false;

            if (this.CancellationRequested)
            {
                // Already cancelled, but the handler has not returned yet — cancellation is
                // cooperative, so it may not for a while. Keep renewing until it does: stopping here
                // would let the claim go stale and another host would run the job a second time
                // while this one is still winding down.
                continue;
            }

            if (maxRuntime is { } limit && now - startedAt >= limit)
            {
                // A hung job would otherwise block its schedule indefinitely through the overlap
                // check. Cancelling turns it into a bounded, visible outcome instead.
                LogMaxRuntimeExceeded(logger, run.JobKey, limit);
                Volatile.Write(ref this.cancellationRequested, 1);
                await jobCancellation.CancelAsync();
                continue;
            }

            if (beat.CancellationRequested)
            {
                Volatile.Write(ref this.cancellationRequested, 1);
                await jobCancellation.CancelAsync();
            }
        }

        // Renewals come every interval, but never later than the fence: with the interval not dividing
        // ClaimTimeout, a host whose renewals keep failing would otherwise notice only a beat late. The
        // fence is that close only after a failed renewal; a landed one puts it ClaimTimeout away.
        TimeSpan WaitBefore(DateTimeOffset fenceAt)
        {
            var untilFence = fenceAt - timeProvider.GetUtcNow();
            return untilFence < options.HeartbeatInterval
                ? TimeSpan.FromTicks(Math.Max(untilFence.Ticks, 0))
                : options.HeartbeatInterval;
        }
    }

    private async Task FenceAsync(
        JobRun run, JobsOptions options, CancellationTokenSource jobCancellation, ILogger logger)
    {
        LogClaimFenced(logger, run.JobKey, options.ClaimTimeout);
        Volatile.Write(ref this.claimLost, 1);
        await jobCancellation.CancelAsync();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job run '{JobKey}' exceeded its MaxRuntime of {MaxRuntime} and is being cancelled")]
    private static partial void LogMaxRuntimeExceeded(ILogger logger, string jobKey, TimeSpan maxRuntime);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Claim on job run '{JobKey}' was taken by another host; stopping this one")]
    private static partial void LogClaimLost(ILogger logger, string jobKey);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Claim on job run '{JobKey}' could not be renewed for its ClaimTimeout of {ClaimTimeout}, after which " +
            "another host may take it; stopping this one")]
    private static partial void LogClaimFenced(ILogger logger, string jobKey, TimeSpan claimTimeout);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat for job run '{JobKey}' failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, string jobKey, Exception exception);
}
