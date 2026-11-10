using System.Data.Common;
using Conduit.Durability.Persistence.Outbox;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// Decorates a real <see cref="IOutboxStore"/> and blocks the first call to
/// <see cref="ClaimByMessageIdsAsync"/> on a <see cref="SemaphoreSlim"/> until the test releases it.
/// </summary>
/// <remarks>
/// Used to deterministically prove a caller returned BEFORE background delivery ran, instead of
/// racing a timing-based assertion against the real (started) <c>ConduitBackgroundDispatcher</c>
/// hosted service. <c>OutboxCapture.ClaimAndDeliverAsync</c> calls <c>ClaimByMessageIdsAsync</c> as
/// its very first operation, so gating it here blocks the ENTIRE delivery routine — no claim, no
/// local dispatch, no transport send, no mark-sent — until the test releases the gate. Construct
/// with a <see cref="SemaphoreSlim"/> whose initial count is 0 (held) and call
/// <see cref="SemaphoreSlim.Release()"/> from the test once its "delivery hasn't happened yet"
/// assertion has run.
/// </remarks>
internal sealed class GatedOutboxStore(IOutboxStore inner, SemaphoreSlim gate) : IOutboxStore
{
    public Task SaveAsync(IReadOnlyList<OutboxEntry> entries, DbTransaction transaction, CancellationToken cancellationToken)
        => inner.SaveAsync(entries, transaction, cancellationToken);

    public Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(int batchSize, TimeSpan gracePeriod, CancellationToken cancellationToken)
        => inner.ClaimPendingAsync(batchSize, gracePeriod, cancellationToken);

    public Task MarkSentAsync(long entryId, CancellationToken cancellationToken)
        => inner.MarkSentAsync(entryId, cancellationToken);

    public Task IncrementRetryAsync(long entryId, TimeSpan retryDelay, CancellationToken cancellationToken)
        => inner.IncrementRetryAsync(entryId, retryDelay, cancellationToken);

    public Task MarkFailedAsync(long entryId, CancellationToken cancellationToken)
        => inner.MarkFailedAsync(entryId, cancellationToken);

    public Task<FailedOutboxPage> QueryFailedAsync(FailedOutboxFilter filter, CancellationToken cancellationToken)
        => inner.QueryFailedAsync(filter, cancellationToken);

    public Task<bool> RetryFailedAsync(long entryId, CancellationToken cancellationToken)
        => inner.RetryFailedAsync(entryId, cancellationToken);

    public Task<int> RetryFailedAsync(string messageType, CancellationToken cancellationToken)
        => inner.RetryFailedAsync(messageType, cancellationToken);

    public Task DeleteCompletedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => inner.DeleteCompletedAsync(retentionPeriod, cancellationToken);

    public Task DeleteFailedAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => inner.DeleteFailedAsync(retentionPeriod, cancellationToken);

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => inner.EnsureTableExistsAsync(cancellationToken);

    public async Task<IReadOnlyList<ClaimedOutboxEntry>> ClaimByMessageIdsAsync(IReadOnlyList<string> messageIds, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        gate.Release();
        return await inner.ClaimByMessageIdsAsync(messageIds, cancellationToken);
    }
}
