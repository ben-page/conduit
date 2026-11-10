namespace Conduit.Jobs.Runtime;

/// <summary>
/// Wakes the durable scheduler's sweep when this process has written work the sweep should take now: a
/// manual run, a delayed trigger's run, a delayed coalescing scope, or a run that has just ended (its end may
/// free a coalescing key or the overlap lock, or schedule a retry sooner than the sweep meant to wake). The
/// sweep computes its sleep from what was due when it last looked, so without this a run queued meanwhile
/// waited for the sweep's next pass, up to <c>ClaimTimeout</c>.
/// </summary>
/// <remarks>
/// In-process only: work written on another host wakes that host's sweep, not this one, and work written on
/// a host with no sweep (<c>ManageOnly</c>) waits for a runner host's next pass. Registered whenever jobs are
/// durable, manage-only included, so <see cref="DurableJobs"/> can always wake it; with no sweep listening a
/// wake does nothing. A <see cref="TaskCompletionSource"/> rather than a semaphore: nothing to dispose, so a
/// run left executing past shutdown can still wake it harmlessly.
/// </remarks>
internal sealed class JobSweepSignal
{
    private TaskCompletionSource current = NewSource();

    /// <summary>
    /// Wakes the sweep: ends the wait it is in, or, while it is mid-pass, makes it go round again instead of
    /// sleeping. Waking an already woken sweep does nothing more. Call it after the write it announces has
    /// committed, so the pass it causes reads that write.
    /// </summary>
    public void Wake() => Volatile.Read(ref this.current).TrySetResult();

    /// <summary>
    /// Called by the sweep as each pass begins. The task it returns completes at the first <see cref="Wake"/>
    /// after this call; a wake before it is spent, because the pass about to run reads whatever that wake
    /// announced.
    /// </summary>
    /// <returns>A task that completes when the sweep is next woken.</returns>
    public Task Arm()
    {
        var source = NewSource();
        Interlocked.Exchange(ref this.current, source);
        return source.Task;
    }

    // Asynchronous continuations, so the caller of Wake (a trigger-now, an event listener, a finishing job)
    // never runs the sweep's next pass on its own thread.
    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
