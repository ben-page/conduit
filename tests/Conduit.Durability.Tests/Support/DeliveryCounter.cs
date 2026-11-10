namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A singleton count of how many times a flaky handler or listener has run, across the DI scopes of every delivery.
/// </summary>
public sealed class DeliveryCounter
{
    private int count;

    /// <summary>How many times it has run.</summary>
    public int Count => Volatile.Read(ref this.count);

    /// <summary>Records a run and returns its number, counting from 1.</summary>
    public int Next() => Interlocked.Increment(ref this.count);
}
