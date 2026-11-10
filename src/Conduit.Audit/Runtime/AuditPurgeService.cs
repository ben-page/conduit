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
    private readonly TimeProvider timeProvider;

    /// <summary>Initializes a new instance of <see cref="AuditPurgeService"/>.</summary>
    /// <param name="scopeFactory">Factory used to scope each purge.</param>
    /// <param name="options">The audit options holding the retention and the purge interval.</param>
    /// <param name="timeProvider">The clock the retention cutoff counts back from; null is <see cref="TimeProvider.System"/>.</param>
    public AuditPurgeService(
        IServiceScopeFactory scopeFactory,
        IOptions<AuditOptions> options,
        TimeProvider? timeProvider = null)
        : base(scopeFactory)
    {
        this.options = options;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    protected override TimeSpan Interval => this.options.Value.PurgeInterval;

    /// <inheritdoc/>
    protected override Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IAuditStore>();
        var cutoff = this.timeProvider.GetUtcNow().UtcDateTime - this.options.Value.RetentionPeriod;
        return store.PurgeExpiredAsync(cutoff, cancellationToken);
    }
}
