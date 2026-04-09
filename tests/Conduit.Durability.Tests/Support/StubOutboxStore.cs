using Conduit.Durability.Store;
using System.Data.Common;

namespace Conduit.Durability.Tests.Support;

public class StubOutboxStore : IOutboxStore
{
    public List<OutboxEntry> SavedEntries { get; } = [];
    public List<long> MarkedSent { get; } = [];
    public List<long> RetriesIncremented { get; } = [];
    public List<long> MarkedFailed { get; } = [];

    public virtual Task SaveAsync(IReadOnlyList<OutboxEntry> entries, DbTransaction transaction, CancellationToken cancellationToken)
    {
        this.SavedEntries.AddRange(entries);
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, TimeSpan gracePeriod, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<OutboxEntry>>([]);

    public virtual Task MarkSentAsync(long entryId, CancellationToken cancellationToken)
    {
        this.MarkedSent.Add(entryId);
        return Task.CompletedTask;
    }

    public virtual Task IncrementRetryAsync(long entryId, CancellationToken cancellationToken)
    {
        this.RetriesIncremented.Add(entryId);
        return Task.CompletedTask;
    }

    public virtual Task MarkFailedAsync(long entryId, CancellationToken cancellationToken)
    {
        this.MarkedFailed.Add(entryId);
        return Task.CompletedTask;
    }

    public virtual Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public virtual Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public IReadOnlyList<ClaimedOutboxEntry> ClaimedEntries { get; set; } = [];

    public virtual Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimByMessageIdsAsync(
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ClaimedOutboxEntry>>(this.ClaimedEntries);
    }
}
