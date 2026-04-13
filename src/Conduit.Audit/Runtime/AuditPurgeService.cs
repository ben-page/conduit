using Conduit.Audit.Configuration;
using Conduit.Audit.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Background service that periodically deletes expired audit records
/// based on the configured <see cref="AuditOptions.RetentionPeriod"/>.
/// </summary>
public sealed class AuditPurgeService(
    IAuditStore store,
    IOptions<AuditOptions> options) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var cutoff = DateTime.UtcNow - options.Value.RetentionPeriod;
            await store.PurgeExpiredAsync(cutoff, stoppingToken);
            await Task.Delay(options.Value.PurgeInterval, stoppingToken);
        }
    }
}
