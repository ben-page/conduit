namespace Conduit.Audit.Tests.Support;

/// <summary>
/// When registered, holds every <see cref="Conduit.Audit.Runtime.AuditBatch"/> in <see cref="GatedAuditBatchListener"/>
/// until <see cref="Release"/>: the publisher is stuck in a publish, as it is behind a slow audit store. A host without
/// it never waits.
/// </summary>
public sealed class AuditBatchGate
{
    private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits in the listener until the gate is released, after signalling that a batch is held.</summary>
    public Task HoldAsync()
    {
        this.held.TrySetResult();
        return this.released.Task;
    }

    /// <summary>Lets every held batch, and every later one, through.</summary>
    public void Release() => this.released.TrySetResult();

    /// <summary>Completes once the listener holds its first batch, or throws after ten seconds.</summary>
    public Task WaitUntilHeldAsync() => this.held.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
