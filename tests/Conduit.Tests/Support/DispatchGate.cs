namespace Conduit.Tests.Support;

/// <summary>
/// Holds every dispatch that passes <see cref="GatedPipeline{TRequest, TResult}"/> until the test opens
/// it, ignoring cancellation, as a long-running handler would.
/// </summary>
public sealed class DispatchGate
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a dispatch is waiting at the gate.</summary>
    public Task Entered => this.entered.Task;

    /// <summary>Lets every waiting dispatch, and every later one, through.</summary>
    public void Open() => this.opened.TrySetResult();

    internal async Task PassAsync()
    {
        this.entered.TrySetResult();
        await this.opened.Task;
    }
}
