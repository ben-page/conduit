using System.Threading.Channels;
using Conduit.Audit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Background service that drains <see cref="DispatchAuditRecord"/> instances from the audit channel
/// and publishes them as <see cref="AuditBatch"/> events via the mediator. Each batch is published
/// within a new DI scope to ensure correct handler lifetime management.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AuditBatch"/> is a plain event, so its listeners run inline on this loop's publish, and a listener's
/// failure (an audit store that cannot be written, a deadlock against the retention purge) throws out of it. The loop
/// logs that failure, drops the batch, and drains the next one: audit is best-effort, and escaping, the failure would
/// end the service, and with the host's default <c>BackgroundServiceExceptionBehavior</c> (<c>StopHost</c>) the host
/// with it. Every publish runs under <see cref="CancellationToken.None"/>, so a stop never cancels a store write
/// part-way.
/// </para>
/// <para>
/// On stop (<see cref="StopAsync"/>) the channel's writer is completed, so the observer's later writes are refused, and
/// the loop drains what is queued until the channel is empty, <see cref="AuditOptions.ShutdownGrace"/> passes or the
/// host's shutdown token fires, whichever is first. The records still unpublished then are counted in one Warning
/// (EventId 520, <c>AuditRecordsAbandoned</c>), and the stop returns without throwing. A stale-group sweep that throws
/// is logged (EventId 521, <c>StaleGroupSweepFailed</c>) and the next sweep runs as usual. A batch's
/// <see cref="AuditBatch.BatchTimestamp"/> and the stale-group sweep's "now" read <paramref name="timeProvider"/> (null
/// is <see cref="TimeProvider.System"/>); the sweep's cadence and the shutdown grace are real time.
/// </para>
/// </remarks>
public sealed partial class AuditBatchPublisher(
    ChannelReader<DispatchAuditRecord> channel,
    ChannelWriter<DispatchAuditRecord> writer,
    IServiceProvider serviceProvider,
    IOptions<AuditOptions> options,
    ILogger<AuditBatchPublisher> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // Guards the hand-off between the drain loop taking a batch and a stop counting what it abandons: once abandoned,
    // the loop takes nothing more, and the count is exact.
    private readonly Lock drainGate = new();
    private int inFlightCount;
    private bool abandoned;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Drain published records and periodically sweep stale (inactive) dispatch groups
        // out of the observer concurrently. The sweep keeps activeGroups from leaking when a
        // dispatch crashes mid-handler and never emits a terminal observation.
        var drainTask = this.DrainLoopAsync();
        var sweepTask = this.SweepLoopAsync(stoppingToken);
        await Task.WhenAll(drainTask, sweepTask);
    }

    /// <summary>
    /// Completes the channel's writer, then waits for the queued records to be published, until
    /// <see cref="AuditOptions.ShutdownGrace"/> passes or <paramref name="cancellationToken"/> fires, whichever is first;
    /// the records left unpublished then are logged (EventId 520) and abandoned. Never throws for either deadline.
    /// </summary>
    /// <param name="cancellationToken">The host's shutdown token.</param>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // The observer's writes are refused from here on, so the drain loop ends once the channel is empty.
        writer.TryComplete();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.Value.ShutdownGrace);

        // Ends the sweep, and waits for the drain until the deadline at most; the base returns, never throws, when the
        // deadline fires first.
        await base.StopAsync(deadline.Token);

        var abandonedCount = this.Abandon();
        if (abandonedCount > 0)
        {
            LogRecordsAbandoned(logger, abandonedCount, options.Value.ShutdownGrace);
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        // Disposed without a stop: end the drain loop rather than leave it waiting on a channel nobody writes to.
        writer.TryComplete();
        this.Abandon();
        base.Dispose();
    }

    private async Task DrainLoopAsync()
    {
        // Not the stopping token: a stop completes the writer, and the loop ends once it has drained the channel, or
        // once the shutdown grace has ended and the stop has abandoned the rest.
        while (await channel.WaitToReadAsync())
        {
            var batch = this.TakeBatch();
            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                using var scope = serviceProvider.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

                // Not the stopping token either: a store write the stop cancelled part-way would lose the batch it is
                // draining for. The stop's grace bounds the wait for it instead.
                await mediator.PublishAsync(
                    new AuditBatch(
                        SourceService: options.Value.ServiceName ?? "unknown",
                        SourceHost: Environment.MachineName,
                        BatchTimestamp: this.clock.GetUtcNow().UtcDateTime,
                        Records: batch),
                    CancellationToken.None);
            }
#pragma warning disable CA1031 // One failed batch must not stop the host or end audit; the next batch is published as usual.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogPublishFailed(logger, batch.Count, exception);
            }
            finally
            {
                lock (this.drainGate)
                {
                    this.inFlightCount = 0;
                }
            }
        }
    }

    private async Task SweepLoopAsync(CancellationToken stoppingToken)
    {
        var observer = serviceProvider.GetRequiredService<AuditObserver>();
        var staleTimeout = options.Value.StaleGroupTimeout;

        // Sweep at the stale-group cadence, capped so very long timeouts still get periodic checks.
        var sweepInterval = staleTimeout < TimeSpan.FromMinutes(1)
            ? staleTimeout
            : TimeSpan.FromMinutes(1);
        using var timer = new PeriodicTimer(sweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    observer.EvictStaleGroups(this.clock.GetUtcNow().UtcDateTime, staleTimeout);
                }
#pragma warning disable CA1031 // One failed sweep must not end the service (and, by default, the host); the next tick sweeps again.
                catch (Exception exception)
#pragma warning restore CA1031
                {
                    LogSweepFailed(logger, exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
    }

    private List<DispatchAuditRecord> TakeBatch()
    {
        lock (this.drainGate)
        {
            var records = new List<DispatchAuditRecord>(options.Value.BatchSize);
            if (this.abandoned)
            {
                return records;
            }

            while (records.Count < options.Value.BatchSize && channel.TryRead(out var record))
            {
                records.Add(record);
            }

            this.inFlightCount = records.Count;
            return records;
        }
    }

    // Stops the drain loop taking batches and counts what it leaves unpublished: the batch in flight, if any, and the
    // records still queued (read off and dropped, so the loop, the writer being complete, finds the channel finished).
    private int Abandon()
    {
        lock (this.drainGate)
        {
            this.abandoned = true;
            var count = this.inFlightCount;
            while (channel.TryRead(out _))
            {
                count++;
            }

            return count;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An audit batch of {RecordCount} record(s) could not be published; its records are dropped and the next batch is published as usual")]
    private static partial void LogPublishFailed(ILogger logger, int recordCount, Exception exception);

    /// <summary>
    /// The stop ended the drain with records unpublished: the shutdown grace passed, or the host's shutdown token fired,
    /// first. EventId 520 is a contract: never renumber it.
    /// </summary>
    [LoggerMessage(
        EventId = 520,
        EventName = "AuditRecordsAbandoned",
        Level = LogLevel.Warning,
        Message = "{RecordCount} audit record(s) were not published when the audit publisher stopped: the shutdown grace " +
            "({ShutdownGrace}) or the host's shutdown deadline ended the drain first")]
    private static partial void LogRecordsAbandoned(ILogger logger, int recordCount, TimeSpan shutdownGrace);

    /// <summary>A stale-group sweep threw; the sweep loop carries on. EventId 521 is a contract: never renumber it.</summary>
    [LoggerMessage(
        EventId = 521,
        EventName = "StaleGroupSweepFailed",
        Level = LogLevel.Error,
        Message = "The audit stale-group sweep failed; the next sweep runs as usual")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
