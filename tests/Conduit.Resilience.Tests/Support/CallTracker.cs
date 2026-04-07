namespace Conduit.Resilience.Tests.Support;

/// <summary>
/// Singleton state tracker that records call counts across transient handler instances.
/// Inject into a transient handler to track retries without losing state between invocations.
/// </summary>
public class CallTracker
{
    private int callCount;

    public int CallCount => this.callCount;

    public int IncrementAndGet() => Interlocked.Increment(ref this.callCount);
}
