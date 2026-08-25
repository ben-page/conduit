using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Deletes terminal runs once they are past retention. Successful runs are already gone — their rows
/// are deleted on completion, which is what keeps the ledger's size proportional to work in flight
/// rather than to job volume. Failed and cancelled rows are the exception: they are kept so an
/// operator can see them, so something has to remove them eventually.
/// </summary>
internal sealed class JobCleanup : PeriodicCleanupServiceBase
{
    private readonly IOptions<JobsOptions> options;
    private readonly TimeProvider timeProvider;

    public JobCleanup(
        IServiceScopeFactory scopeFactory,
        IOptions<JobsOptions> options,
        TimeProvider timeProvider)
        : base(scopeFactory)
    {
        this.options = options;
        this.timeProvider = timeProvider;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override Task CleanupAsync(IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IJobStore>();
        var cutoff = this.timeProvider.GetUtcNow() - this.options.Value.FailedRetention;
        return store.PurgeTerminalAsync(cutoff, cancellationToken);
    }
}
