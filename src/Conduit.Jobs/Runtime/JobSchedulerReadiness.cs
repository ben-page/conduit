namespace Conduit.Jobs.Runtime;

/// <summary>
/// Completes once every scheduling loop has armed its first wait, so a host can be observed to be
/// <i>scheduling</i> rather than merely <i>started</i>.
/// </summary>
/// <remarks>
/// A hosted service's <c>StartAsync</c> returning does not mean its loops have reached their first
/// wait. Under a real clock that is invisible — a timer armed a moment late simply fires a moment
/// late. Under a discontinuous clock it is not: a test that moves a
/// <see cref="TimeProvider"/> forward before the timer exists moves past an occurrence nothing was
/// waiting for. Awaiting <see cref="Ready"/> closes that window.
/// </remarks>
internal sealed class JobSchedulerReadiness
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int remaining = -1;

    /// <summary>Completes when every expected loop has armed its first wait.</summary>
    public Task Ready => this.ready.Task;

    /// <summary>Declares how many loops will report. Called once, before any of them starts.</summary>
    /// <param name="loops">The number of loops to wait for.</param>
    public void Expect(int loops)
    {
        Interlocked.Exchange(ref this.remaining, loops);
        if (loops <= 0)
        {
            this.ready.TrySetResult();
        }
    }

    /// <summary>Reports that one loop has armed its first wait.</summary>
    public void ReportArmed()
    {
        if (Interlocked.Decrement(ref this.remaining) <= 0)
        {
            this.ready.TrySetResult();
        }
    }
}
