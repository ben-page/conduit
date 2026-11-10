using Conduit.Durability.Persistence.Outbox;

namespace Conduit.Durability.Tests.Support;

/// <summary>A store with nothing pending that counts the relay's sweeps (each one claims once).</summary>
public sealed class SweepCountingOutboxStore : StubOutboxStore
{
    private int sweeps;

    /// <summary>The claims the relay has made so far, one per sweep.</summary>
    public int Sweeps => Volatile.Read(ref this.sweeps);

    public override Task<IReadOnlyList<OutboxEntry>> ClaimPendingAsync(
        int batchSize,
        TimeSpan gracePeriod,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref this.sweeps);
        return base.ClaimPendingAsync(batchSize, gracePeriod, cancellationToken);
    }
}
