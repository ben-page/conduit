using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Deletes terminal runs once they are past retention, and history entries once they are past their
/// own (longer) retention. Most successful runs are already gone from the ledger — their rows are
/// deleted on completion, which keeps the ledger's size proportional to work in flight rather than to
/// job volume. The exceptions are kept on purpose: failed and cancelled rows so an operator can see
/// them, and completed non-coalescing trigger runs so a late redelivery of their event still collapses
/// on the run key. Something has to remove each of them eventually. History is unbounded by design until this
/// runs — every terminal run writes a row there, independent of the ledger's own retention.
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

    protected override async Task CleanupAsync(
        IServiceProvider scopedProvider, CancellationToken cancellationToken)
    {
        var store = scopedProvider.GetRequiredService<IJobStore>();
        var now = this.timeProvider.GetUtcNow();

        await store.PurgeTerminalAsync(now - this.options.Value.FailedRetention, cancellationToken);
        await store.PurgeCompletedAsync(now - this.options.Value.CompletedRetention, cancellationToken);
        await store.PurgeHistoryAsync(now - this.options.Value.HistoryRetention, cancellationToken);
    }
}
