using Conduit.Durability.Persistence.Outbox;
using System.Data.Common;

namespace Conduit.Durability.Tests.Support;

public class StubOutboxStore : IOutboxStore
{
    public List<OutboxEntry> SavedEntries { get; } = [];
    public List<long> MarkedSent { get; } = [];
    public List<long> RetriesIncremented { get; } = [];
    public List<TimeSpan> RetryDelays { get; } = [];
    public List<long> MarkedFailed { get; } = [];
    public List<TimeSpan> DeletedRetentions { get; } = [];

    /// <summary>The claim token every MarkSent, IncrementRetry and MarkFailed call passed, in call order.</summary>
    public List<string> ResolvedClaimTokens { get; } = [];

    /// <summary>
    /// When true, every MarkSent, IncrementRetry and MarkFailed call finds the claim gone and returns false, as the
    /// store does once another worker has re-claimed the row.
    /// </summary>
    public bool ClaimLost { get; set; }

    public virtual Task SaveAsync(IReadOnlyList<OutboxEntry> entries, DbTransaction transaction, CancellationToken cancellationToken)
    {
        this.SavedEntries.AddRange(entries);
        return Task.CompletedTask;
    }

    public virtual Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, TimeSpan gracePeriod, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<OutboxEntry>>([]);

    public virtual Task<bool> MarkSentAsync(long entryId, string claimToken, CancellationToken cancellationToken)
    {
        this.MarkedSent.Add(entryId);
        this.ResolvedClaimTokens.Add(claimToken);
        return Task.FromResult(!this.ClaimLost);
    }

    public virtual Task<bool> IncrementRetryAsync(long entryId, string claimToken, TimeSpan retryDelay, CancellationToken cancellationToken)
    {
        this.RetriesIncremented.Add(entryId);
        this.RetryDelays.Add(retryDelay);
        this.ResolvedClaimTokens.Add(claimToken);
        return Task.FromResult(!this.ClaimLost);
    }

    public virtual Task<bool> MarkFailedAsync(long entryId, string claimToken, CancellationToken cancellationToken)
    {
        this.MarkedFailed.Add(entryId);
        this.ResolvedClaimTokens.Add(claimToken);
        return Task.FromResult(!this.ClaimLost);
    }

    public virtual Task<FailedOutboxPage> QueryFailedAsync(FailedOutboxFilter filter, CancellationToken cancellationToken)
        => Task.FromResult(new FailedOutboxPage { Entries = [], NextPageToken = null, TotalCount = 0 });

    public virtual Task<bool> RetryFailedAsync(long entryId, CancellationToken cancellationToken)
        => Task.FromResult(false);

    public virtual Task<int> RetryFailedAsync(string messageType, CancellationToken cancellationToken)
        => Task.FromResult(0);

    public virtual Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        this.DeletedRetentions.Add(retentionPeriod);
        return Task.CompletedTask;
    }

    public List<TimeSpan> DeletedFailedRetentions { get; } = [];

    public virtual Task DeleteFailedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
    {
        this.DeletedFailedRetentions.Add(retentionPeriod);
        return Task.CompletedTask;
    }

    public virtual Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;

    public IReadOnlyList<ClaimedOutboxEntry> ClaimedEntries { get; set; } = [];

    /// <summary>Whether <see cref="ClaimByMessageIdsAsync"/> was ever called — proves (or disproves) that a delivery attempt ran.</summary>
    public bool ClaimByMessageIdsCalled { get; private set; }

    public virtual Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimByMessageIdsAsync(
        IReadOnlyList<string> messageIds,
        CancellationToken cancellationToken)
    {
        this.ClaimByMessageIdsCalled = true;
        return Task.FromResult<IReadOnlyList<ClaimedOutboxEntry>>(this.ClaimedEntries);
    }
}
