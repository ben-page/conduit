using System.Collections.Concurrent;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// Records job starts and completions. Thread-safe — runs execute on background threads. Starts and
/// completions are tracked separately because the overlap and shutdown tests assert on runs that
/// begin but never finish.
/// </summary>
public sealed class JobSpy
{
    private readonly ConcurrentQueue<string> started = new();
    private readonly ConcurrentQueue<string> completed = new();
    private int failuresRemaining;

    /// <summary>Runs that began.</summary>
    public int StartedCount => this.started.Count;

    /// <summary>Runs that ran to completion.</summary>
    public int Count => this.completed.Count;

    /// <summary>Every label a run started with.</summary>
    public IReadOnlyList<string> Started => [.. this.started];

    /// <summary>
    /// How long a handler blocks before completing, measured on the injected
    /// <see cref="TimeProvider"/> so a "ten-minute job" is deterministic. Zero returns immediately.
    /// </summary>
    public TimeSpan JobDuration { get; set; }

    /// <summary>
    /// When set, the handler waits without honouring its <see cref="CancellationToken"/>, standing in
    /// for a handler that never checks it. Cancellation is cooperative, so such a job runs to
    /// completion however hard it is cancelled.
    /// </summary>
    public bool IgnoresCancellation { get; set; }

    /// <summary>How many further invocations throw before one is allowed to succeed.</summary>
    public int FailuresRemaining
    {
        get => Volatile.Read(ref this.failuresRemaining);
        set => Volatile.Write(ref this.failuresRemaining, value);
    }

    /// <summary>Records that a run began.</summary>
    public void RecordStart(string label) => this.started.Enqueue(label);

    /// <summary>Records that a run finished.</summary>
    public void RecordCompletion(string label) => this.completed.Enqueue(label);

    /// <summary>Consumes one scheduled failure, if any remain.</summary>
    public bool ShouldFail() => Interlocked.Decrement(ref this.failuresRemaining) >= 0;

    /// <summary>Waits until at least <paramref name="expected"/> runs have completed.</summary>
    public Task WaitForCountAsync(int expected, TimeSpan? timeout = null)
        => WaitAsync(() => this.Count, expected, "completed", timeout);

    /// <summary>Waits until at least <paramref name="expected"/> runs have started.</summary>
    public Task WaitForStartedAsync(int expected, TimeSpan? timeout = null)
        => WaitAsync(() => this.StartedCount, expected, "started", timeout);

    private static async Task WaitAsync(Func<int> read, int expected, string what, TimeSpan? timeout)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (read() < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Expected {expected} {what} job(s) but saw {read()}.");
            }

            await Task.Delay(10);
        }
    }
}
