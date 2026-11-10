using System.Collections.Concurrent;
using Conduit.Audit.Persistence;
using Conduit.Audit.Runtime;

namespace Conduit.Audit.Tests.Support;

/// <summary>An audit store that keeps nothing and records the cutoff of every purge it is asked for.</summary>
public sealed class PurgeRecordingAuditStore : IAuditStore
{
    private readonly ConcurrentQueue<DateTime> cutoffs = new();

    /// <summary>Every purge's cutoff, in order.</summary>
    public IReadOnlyList<DateTime> Cutoffs => [.. this.cutoffs];

    public Task SaveBatchAsync(IReadOnlyList<DispatchAuditRecord> records, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task<DispatchAuditRecord?> GetByMessageIdAsync(string messageId, CancellationToken cancellationToken)
        => Task.FromResult<DispatchAuditRecord?>(null);

    public Task PurgeExpiredAsync(DateTime cutoff, CancellationToken cancellationToken)
    {
        this.cutoffs.Enqueue(cutoff);
        return Task.CompletedTask;
    }

    public Task EnsureTableExistsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
