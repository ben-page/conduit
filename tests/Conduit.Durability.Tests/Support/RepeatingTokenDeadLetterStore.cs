using Conduit.Durability.DeadLetterQueue;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A custom store whose every page holds its one entry and hands back the same page token, as a store with a paging
/// bug might. It refuses a fourth query, so a caller that follows the token forever fails instead of hanging.
/// </summary>
internal sealed class RepeatingTokenDeadLetterStore(DeadLetterEntry entry) : IDeadLetterStore
{
    private int queries;

    public bool Removed { get; private set; }

    public Task WriteAsync(DeadLetterEntry deadLetter, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<DeadLetterEntry?>(id == entry.Id && !this.Removed ? entry : null);

    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken)
    {
        if (++this.queries > 3)
        {
            throw new InvalidOperationException("Queried past the store's last page.");
        }

        return Task.FromResult(new DeadLetterPage
        {
            Entries = [entry],
            NextPageToken = "1",
            TotalCount = 1,
        });
    }

    public Task<string?> ClaimForReplayAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<string?>(id == entry.Id && !this.Removed ? "claim" : null);

    public Task ReleaseReplayClaimAsync(Guid id, string claimToken, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        this.Removed |= id == entry.Id;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        this.Removed |= ids.Contains(entry.Id);
        return Task.CompletedTask;
    }

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
