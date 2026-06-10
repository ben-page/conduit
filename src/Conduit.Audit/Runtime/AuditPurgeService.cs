using Conduit.Audit.Configuration;
using Conduit.Audit.Persistence;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Background service that periodically deletes expired audit records based on the configured
/// <see cref="AuditOptions.RetentionPeriod"/>. Extends <see cref="PeriodicCleanupServiceBase"/>, which
/// runs each tick inside a fresh DI scope so the scoped <see cref="IAuditStore"/> can be resolved
/// safely — injecting the scoped store directly into this singleton would be a captive dependency.
/// </summary>
public sealed class AuditPurgeService : PeriodicCleanupServiceBase
{
    private readonly IOptions<AuditOptions> options;

    /// <summary>Initializes a new instance of <see cref="AuditPurgeService"/>.</summary>
    public AuditPurgeService(IServiceScopeFactory scopeFactory, IOptions<AuditOptions> options)
        : base(scopeFactory)
    {
        this.options = options;
    }

    /// <inheritdoc/>
    protected override TimeSpan Interval => this.options.Value.PurgeInterval;

    /// <inheritdoc/>
    protected override Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IAuditStore>();
        var cutoff = DateTime.UtcNow - this.options.Value.RetentionPeriod;
        return store.PurgeExpiredAsync(cutoff, cancellationToken);
    }
}
