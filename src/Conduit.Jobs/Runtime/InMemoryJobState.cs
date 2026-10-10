using System.Collections.Concurrent;

namespace Conduit.Jobs.Runtime;

/// <summary>
/// Pause and cancellation state for the non-durable scheduler. Held in memory, so — like the
/// scheduler itself — it is per-host and lost on restart. That is the honest shape of the
/// no-database deployment, not a shortcut: there is nowhere else to put it.
/// </summary>
internal sealed class InMemoryJobState
{
    private readonly ConcurrentDictionary<string, bool> paused = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running =
        new(StringComparer.Ordinal);

    /// <summary>Whether the schedule's future occurrences are suppressed on this host.</summary>
    public bool IsPaused(string scheduleName) => this.paused.ContainsKey(scheduleName);

    /// <summary>Suppresses or resumes a schedule's future occurrences on this host.</summary>
    public void SetPaused(string scheduleName, bool value)
    {
        if (value)
        {
            this.paused[scheduleName] = true;
        }
        else
        {
            this.paused.TryRemove(scheduleName, out _);
        }
    }

    /// <summary>
    /// Registers a running job so a cancellation request can reach it, unless a run of the schedule is
    /// already registered. Atomic, so it is also this host's overlap check: of a scheduled run and a
    /// manual one starting together, exactly one registers.
    /// </summary>
    /// <returns><see langword="false"/> when a run of the schedule is already registered.</returns>
    public bool TryTrack(string scheduleName, CancellationTokenSource cancellation)
        => this.running.TryAdd(scheduleName, cancellation);

    /// <summary>
    /// Forgets a job that has finished — only the registration it made, so a run that never
    /// registered (it overlapped one that did) cannot remove the other's.
    /// </summary>
    public void Forget(string scheduleName, CancellationTokenSource cancellation)
        => this.running.TryRemove(KeyValuePair.Create(scheduleName, cancellation));

    /// <summary>Cancels a running job's token. Returns false when nothing by that name is running.</summary>
    public bool TryCancel(string scheduleName)
    {
        if (!this.running.TryGetValue(scheduleName, out var cancellation))
        {
            return false;
        }

        cancellation.Cancel();
        return true;
    }
}
