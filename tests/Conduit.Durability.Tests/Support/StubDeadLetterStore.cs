using Conduit.Durability.DeadLetterQueue;

namespace Conduit.Durability.Tests.Support;

internal sealed class StubDeadLetterStore : IDeadLetterStore
{
    private readonly List<DeadLetterEntry> entries = [];

    public IReadOnlyList<DeadLetterEntry> Entries => this.entries;

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
        var filtered = this.entries.AsEnumerable();
        if (filter.MessageType is not null)
        {
            filtered = filtered.Where(e => e.MessageType == filter.MessageType);
        }

        var list = filtered.ToList();
        return Task.FromResult(new DeadLetterPage
        {
            Entries = list,
            NextPageToken = null,
            TotalCount = list.Count,
        });
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken)
    {
        this.entries.RemoveAll(e => e.Id == id);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var idSet = new HashSet<Guid>(ids);
        this.entries.RemoveAll(e => idSet.Contains(e.Id));
        return Task.CompletedTask;
    }

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
