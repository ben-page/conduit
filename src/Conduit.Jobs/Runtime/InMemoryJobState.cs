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
    private readonly ConcurrentDictionary<string, (string RunKey, CancellationTokenSource Cancellation)> running =
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
    /// Registers a running job so a cancellation request can reach it, by its run key or its schedule's
    /// name, unless a run of the schedule is already registered. Atomic, so it is also this host's overlap
    /// check: of a scheduled run and a manual one starting together, exactly one registers.
    /// </summary>
    /// <param name="scheduleName">The schedule the run belongs to.</param>
    /// <param name="runKey">
    /// The run's key, shaped as the durable runner's: <c>{schedule}@manual-{instant}</c> for a manual run,
    /// <c>{schedule}@{occurrence}</c> for an occurrence, <c>{schedule}@start-{instant}</c> for a start-up run.
    /// </param>
    /// <param name="cancellation">The source that cancels the run.</param>
    /// <returns><see langword="false"/> when a run of the schedule is already registered.</returns>
    public bool TryTrack(string scheduleName, string runKey, CancellationTokenSource cancellation)
        => this.running.TryAdd(scheduleName, (runKey, cancellation));

    /// <summary>
    /// Forgets a job that has finished — only the registration it made, so a run that never
    /// registered (it overlapped one that did) cannot remove the other's.
    /// </summary>
    public void Forget(string scheduleName, CancellationTokenSource cancellation)
    {
        if (this.running.TryGetValue(scheduleName, out var tracked) && ReferenceEquals(tracked.Cancellation, cancellation))
        {
            this.running.TryRemove(KeyValuePair.Create(scheduleName, tracked));
        }
    }

    /// <summary>
    /// Cancels a running job's token, found by its run key or, failing that, by its schedule's name: the run
    /// key wins when a key names one run and a schedule at once. Returns false when nothing by that key or
    /// name is running.
    /// </summary>
    /// <remarks>
    /// The source can be disposed between the lookup and the cancel: the run finished, forgot itself and
    /// disposed it. That is the same answer as not finding it — the job is no longer running — rather
    /// than an <see cref="ObjectDisposedException"/> escaping <c>IJobs.CancelAsync</c>.
    /// </remarks>
    public bool TryCancel(string runKeyOrScheduleName)
    {
        var byRunKey = this.running.Values
            .Where(tracked => string.Equals(tracked.RunKey, runKeyOrScheduleName, StringComparison.Ordinal))
            .Select(tracked => tracked.Cancellation)
            .FirstOrDefault();
        var cancellation = byRunKey
            ?? (this.running.TryGetValue(runKeyOrScheduleName, out var byName) ? byName.Cancellation : null);
        if (cancellation is null)
        {
            return false;
        }

        try
        {
            cancellation.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
