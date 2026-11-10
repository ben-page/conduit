using System.Collections.Concurrent;
using Conduit.Durability.Persistence.Inbox;

namespace Conduit.Durability.Tests.Support;

/// <summary>An <see cref="IInboxStore"/> over a set in memory, with the database store's newly-inserted semantics.</summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<string, byte> messages = new(StringComparer.Ordinal);

    /// <summary>When set, every <see cref="RemoveAsync"/> throws it, as an unreachable database would.</summary>
    public Exception? RemoveFailure { get; init; }

    public Task<bool> ExistsAsync(string messageId, CancellationToken cancellationToken)
        => Task.FromResult(this.messages.ContainsKey(messageId));

    // TryAdd is true only when the id is newly added: the rows-affected answer the real store gives.
    public Task<bool> RecordAsync(string messageId, CancellationToken cancellationToken)
        => Task.FromResult(this.messages.TryAdd(messageId, 0));

    public Task RemoveAsync(string messageId, CancellationToken cancellationToken)
    {
        if (this.RemoveFailure is { } failure)
        {
            throw failure;
        }

        this.messages.TryRemove(messageId, out _);
        return Task.CompletedTask;
    }

    public Task DeleteExpiredAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
