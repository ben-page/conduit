using System.Threading.Channels;
using Conduit.Audit.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Background service that drains <see cref="DispatchAuditRecord"/> instances from the audit channel
/// and publishes them as <see cref="AuditBatch"/> events via the mediator. Each batch is published
/// within a new DI scope to ensure correct handler lifetime management.
/// </summary>
public sealed class AuditBatchPublisher(
    ChannelReader<DispatchAuditRecord> channel,
    IServiceProvider serviceProvider,
    IOptions<AuditOptions> options) : BackgroundService
{
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

            using var scope = serviceProvider.CreateScope();
            var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            await mediator.PublishAsync(new AuditBatch(
                SourceService: options.Value.ServiceName ?? "unknown",
                SourceHost: Environment.MachineName,
                BatchTimestamp: DateTime.UtcNow,
                Records: batch), stoppingToken);
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
                observer.EvictStaleGroups(DateTime.UtcNow, staleTimeout);
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
}
