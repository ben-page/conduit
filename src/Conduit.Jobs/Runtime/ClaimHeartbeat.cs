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
        TimeSpan interval,
        CancellationTokenSource jobCancellation,
        ILogger logger)
    {
        this.loop = this.RunAsync(
            run, maxRuntime, scopeFactory, timeProvider, interval, jobCancellation, logger);
    }

    /// <summary>Whether an operator asked for this run to stop, or it outran its <c>MaxRuntime</c>.</summary>
    public bool CancellationRequested => Volatile.Read(ref this.cancellationRequested) != 0;

    /// <summary>Whether another host has taken the claim, in which case this host owns nothing.</summary>
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
            run, maxRuntime, scopeFactory, timeProvider, options.Value.HeartbeatInterval,
            jobCancellation, logger);
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
        TimeSpan interval,
        CancellationTokenSource jobCancellation,
        ILogger logger)
    {
        var startedAt = run.StartedAt ?? timeProvider.GetUtcNow();

        while (!this.stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, timeProvider, this.stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var now = timeProvider.GetUtcNow();

            if (maxRuntime is { } limit && now - startedAt >= limit)
            {
                // A hung job would otherwise block its schedule indefinitely through the overlap
                // check. Cancelling turns it into a bounded, visible outcome instead.
                LogMaxRuntimeExceeded(logger, run.JobKey, limit);
                Volatile.Write(ref this.cancellationRequested, 1);
                await jobCancellation.CancelAsync();
                return;
            }

            HeartbeatResult beat;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
                beat = await store.HeartbeatAsync(run.Id, run.ClaimToken!, now, CancellationToken.None);
            }
#pragma warning disable CA1031 // A missed beat is survivable; the claim only goes stale after several.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogHeartbeatFailed(logger, run.JobKey, exception);
                continue;
            }

            if (!beat.ClaimHeld)
            {
                LogClaimLost(logger, run.JobKey);
                Volatile.Write(ref this.claimLost, 1);
                await jobCancellation.CancelAsync();
                return;
            }

            if (beat.CancellationRequested)
            {
                Volatile.Write(ref this.cancellationRequested, 1);
                await jobCancellation.CancelAsync();
                return;
            }
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job run '{JobKey}' exceeded its MaxRuntime of {MaxRuntime} and is being cancelled")]
    private static partial void LogMaxRuntimeExceeded(ILogger logger, string jobKey, TimeSpan maxRuntime);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Claim on job run '{JobKey}' was taken by another host; stopping this one")]
    private static partial void LogClaimLost(ILogger logger, string jobKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat for job run '{JobKey}' failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, string jobKey, Exception exception);
}
