namespace Conduit.Transport.Kafka.Tests.Support;

/// <summary>
/// Tracks concurrent handler execution. Handlers call <see cref="Enter"/> on entry and
/// <see cref="Exit"/> on exit; <see cref="MaxObservedConcurrency"/> reports the peak number of
/// handlers that were executing simultaneously. Used to assert that a consumer group with
/// concurrency &gt; 1 actually processes messages in parallel across partitions.
/// </summary>
public sealed class ConcurrencyProbe
{
    private readonly Lock gate = new();
    private int current;

    /// <summary>The peak number of handlers observed executing at the same time.</summary>
    public int MaxObservedConcurrency { get; private set; }

    /// <summary>Records a handler entering execution and updates the observed peak.</summary>
    public void Enter()
    {
        lock (this.gate)
        {
            this.current++;
            if (this.current > this.MaxObservedConcurrency)
            {
                this.MaxObservedConcurrency = this.current;
            }
        }
    }

    /// <summary>Records a handler leaving execution.</summary>
    public void Exit()
    {
        lock (this.gate)
        {
            this.current--;
        }
    }
}
