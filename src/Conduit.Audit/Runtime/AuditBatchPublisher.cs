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
/// <see cref="AuditBatch"/> is a plain event, so its listeners run inline on this loop's publish, and a listener's
/// failure (an audit store that cannot be written, a deadlock against the retention purge) throws out of it. The loop
/// logs that failure, drops the batch, and drains the next one: audit is best-effort, and escaping, the failure would
/// end the service, and with the host's default <c>BackgroundServiceExceptionBehavior</c> (<c>StopHost</c>) the host
/// with it. A publish that throws while the host is stopping ends the loop quietly, whatever the exception: that is
/// the stop's cancellation. A batch's <see cref="AuditBatch.BatchTimestamp"/> and the stale-group sweep's "now" read
/// <paramref name="timeProvider"/> (null is <see cref="TimeProvider.System"/>); the sweep's cadence is real time.
/// </remarks>
public sealed partial class AuditBatchPublisher(
    ChannelReader<DispatchAuditRecord> channel,
    IServiceProvider serviceProvider,
    IOptions<AuditOptions> options,
    ILogger<AuditBatchPublisher> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Drain published records and periodically sweep stale (never-terminated) dispatch groups
        // out of the observer concurrently. The sweep keeps activeGroups from leaking when a
        // dispatch crashes mid-handler and never emits a terminal observation.
        var drainTask = this.DrainLoopAsync(stoppingToken);
        var sweepTask = this.SweepLoopAsync(stoppingToken);
        await Task.WhenAll(drainTask, sweepTask);
    }

    private async Task DrainLoopAsync(CancellationToken stoppingToken)
    {
        while (await channel.WaitToReadAsync(stoppingToken))
        {
            var batch = this.DrainBatch();
            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                using var scope = serviceProvider.CreateScope();
                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                await mediator.PublishAsync(new AuditBatch(
                    SourceService: options.Value.ServiceName ?? "unknown",
                    SourceHost: Environment.MachineName,
                    BatchTimestamp: this.clock.GetUtcNow().UtcDateTime,
                    Records: batch), stoppingToken);
            }
#pragma warning disable CA1031 // While stopping, any exception is the stop's cancellation: SqlClient reports it as a SqlException.
            catch (Exception) when (stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
            {
                return;
            }
#pragma warning disable CA1031 // One failed batch must not stop the host or end audit; the next batch is published as usual.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogPublishFailed(logger, batch.Count, exception);
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
                observer.EvictStaleGroups(this.clock.GetUtcNow().UtcDateTime, staleTimeout);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — expected.
        }
    }

    private List<DispatchAuditRecord> DrainBatch()
    {
        var records = new List<DispatchAuditRecord>(options.Value.BatchSize);
        while (records.Count < options.Value.BatchSize && channel.TryRead(out var record))
        {
            records.Add(record);
        }

        return records;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "An audit batch of {RecordCount} record(s) could not be published; its records are dropped and the next batch is published as usual")]
    private static partial void LogPublishFailed(ILogger logger, int recordCount, Exception exception);
}
