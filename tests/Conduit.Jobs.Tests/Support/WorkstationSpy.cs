using System.Collections.Concurrent;

namespace Conduit.Jobs.Tests.Support;

/// <summary>
/// Records the scope each rebuild ran with. Separate from <see cref="JobSpy"/> because the coalescing
/// tests assert on the merged payload a run received, not merely on how many ran.
/// </summary>
public sealed class WorkstationSpy : IDisposable
{
    private readonly ConcurrentQueue<RecomputeWorkstation> rebuilds = new();
    private readonly SemaphoreSlim gate = new(0);

    /// <summary>Every rebuild that began, in order.</summary>
    public IReadOnlyList<RecomputeWorkstation> Rebuilds => [.. this.rebuilds];

    /// <summary>How many rebuilds began.</summary>
    public int Count => this.rebuilds.Count;

    /// <summary>Blocks each rebuild until <see cref="ReleaseOne"/> is called, when set.</summary>
    public bool BlockUntilReleased { get; set; }

    /// <summary>Records a rebuild and, when blocking, waits for its release.</summary>
    public async Task RecordAsync(RecomputeWorkstation command, CancellationToken cancellationToken)
    {
        this.rebuilds.Enqueue(command);

        if (this.BlockUntilReleased)
        {
            await this.gate.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Lets one blocked rebuild finish.</summary>
    public void ReleaseOne() => this.gate.Release();

    /// <summary>Waits until at least <paramref name="expected"/> rebuilds have begun.</summary>
    public async Task WaitForCountAsync(int expected, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (this.Count < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Only {this.Count} of {expected} rebuilds began.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Releases the underlying gate semaphore.</summary>
    public void Dispose() => this.gate.Dispose();
}
