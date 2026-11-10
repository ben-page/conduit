using System.Collections.Concurrent;
using Conduit.Audit.Runtime;

namespace Conduit.Audit.Tests.Support;

/// <summary>A singleton that collects every published <see cref="AuditBatch"/>, in arrival order.</summary>
public sealed class PublishedAuditBatches
{
    private readonly ConcurrentQueue<AuditBatch> batches = new();

    /// <summary>The batches published so far.</summary>
    public IReadOnlyList<AuditBatch> Batches => [.. this.batches];

    public void Add(AuditBatch batch) => this.batches.Enqueue(batch);

    /// <summary>Polls until at least one batch has been published, or throws.</summary>
    public async Task WaitForFirstAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (this.batches.IsEmpty)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("No audit batch was published.");
            }

            await Task.Delay(10);
        }
    }
}
