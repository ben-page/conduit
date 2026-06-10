using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Inbox;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Durability.Runtime.Inbox;

/// <summary>
/// Background service that periodically removes expired inbox entries. Each tick runs in a fresh DI
/// scope so the scoped <see cref="IInboxStore"/> can be resolved safely.
/// </summary>
public sealed class InboxCleanup : PeriodicCleanupServiceBase
{
    private readonly IOptions<InboxOptions> options;

    /// <summary>Initializes a new instance of <see cref="InboxCleanup"/>.</summary>
    public InboxCleanup(IServiceScopeFactory scopeFactory, IOptions<InboxOptions> options)
        : base(scopeFactory)
    {
        this.options = options;
    }

    /// <inheritdoc/>
    protected override TimeSpan Interval => this.options.Value.CleanupInterval;

    /// <inheritdoc/>
    protected override Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IInboxStore>();
        return store.DeleteExpiredAsync(this.options.Value.RetentionPeriod, cancellationToken);
    }
}
