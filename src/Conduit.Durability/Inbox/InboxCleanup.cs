using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Inbox;

/// <summary>
/// Background service that periodically removes expired inbox entries.
/// </summary>
public sealed class InboxCleanup(
    IInboxStore store,
    IOptions<InboxOptions> options) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await store.EnsureTableExistsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await store.DeleteExpiredAsync(options.Value.RetentionPeriod, stoppingToken);
            await Task.Delay(options.Value.CleanupInterval, stoppingToken);
        }
    }
}
