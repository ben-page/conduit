using Conduit.Audit.Runtime;
using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

/// <summary>
/// When registered, makes every <see cref="AuditBatch"/> listener failure observable: <see cref="FailingAuditBatchListener"/>
/// counts each batch here, then throws. A host without it never sees the listener fail.
/// </summary>
public sealed class AuditBatchFailures
{
    private int count;

    /// <summary>The batches the failing listener has received so far.</summary>
    public int Count => Volatile.Read(ref this.count);

    public void Record() => Interlocked.Increment(ref this.count);

    /// <summary>Polls until the failing listener has received at least <paramref name="expected"/> batches, or throws.</summary>
    public async Task WaitForCountAsync(int expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (this.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} failed batch(es) but saw {this.Count}.");
            }

            await Task.Delay(10);
        }
    }
}

/// <summary>An <see cref="AuditBatch"/> listener that throws on every batch, in a host that registers <see cref="AuditBatchFailures"/>.</summary>
public sealed class FailingAuditBatchListener(IServiceProvider services) : IEventListener<AuditBatch>
{
    public Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(AuditBatchFailures)) is not AuditBatchFailures failures)
        {
            return Task.CompletedTask;
        }

        failures.Record();
        throw new InvalidOperationException("the audit store is unreachable");
    }
}
