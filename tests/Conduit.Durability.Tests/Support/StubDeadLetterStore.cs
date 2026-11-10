using System.Globalization;
using Conduit.Durability.DeadLetterQueue;

namespace Conduit.Durability.Tests.Support;

internal sealed class StubDeadLetterStore : IDeadLetterStore
{
    private readonly List<DeadLetterEntry> entries = [];

    // The replay claim each held entry is under. A claim here never times out.
    private readonly Dictionary<Guid, string> replayClaims = [];

    public IReadOnlyList<DeadLetterEntry> Entries => this.entries;

    /// <summary>When true, removing an entry throws, as a store whose database went away mid-replay would.</summary>
    public bool FailRemoval { get; set; }

    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken)
    {
        this.entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = this.entries.FirstOrDefault(e => e.Id == id);
        return Task.FromResult(entry);
    }

    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken)
    {
        // As the real store does: a page is never the whole table.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filter.PageSize, DeadLetterFilter.MaxPageSize);

        var filtered = this.entries.AsEnumerable();
        if (filter.MessageType is not null)
        {
            filtered = filtered.Where(e => e.MessageType == filter.MessageType);
        }

        var list = filtered.ToList();
        var offset = filter.PageToken is null ? 0 : int.Parse(filter.PageToken, CultureInfo.InvariantCulture);
        var page = list.Skip(offset).Take(filter.PageSize).ToList();
        var nextOffset = offset + page.Count;
        return Task.FromResult(new DeadLetterPage
        {
            Entries = page,
            NextPageToken = nextOffset < list.Count ? nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            TotalCount = list.Count,
        });
    }

    public Task<string?> ClaimForReplayAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!this.entries.Any(e => e.Id == id) || this.replayClaims.ContainsKey(id))
        {
            return Task.FromResult<string?>(null);
        }

        var claimToken = Guid.NewGuid().ToString("N");
        this.replayClaims[id] = claimToken;
        return Task.FromResult<string?>(claimToken);
    }

    public Task ReleaseReplayClaimAsync(Guid id, string claimToken, CancellationToken cancellationToken)
    {
        if (this.replayClaims.TryGetValue(id, out var held) && held == claimToken)
        {
            this.replayClaims.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        if (this.FailRemoval)
        {
            throw new InvalidOperationException("The dead-letter store is unreachable.");
        }

        this.entries.RemoveAll(e => e.Id == id);
        this.replayClaims.Remove(id);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var idSet = new HashSet<Guid>(ids);
        this.entries.RemoveAll(e => idSet.Contains(e.Id));
        foreach (var id in idSet)
        {
            this.replayClaims.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
