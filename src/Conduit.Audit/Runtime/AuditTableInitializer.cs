using Conduit.Audit.Persistence;
using Microsoft.Extensions.Hosting;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Hosted service that creates the audit table on application startup
/// when a local audit store is configured.
/// </summary>
internal sealed class AuditTableInitializer(IAuditStore auditStore) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await auditStore.EnsureTableExistsAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
