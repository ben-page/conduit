namespace Conduit.Audit.Tests.Support;

/// <summary>
/// When registered, holds <see cref="HeldAuditCommandHandler"/> until <see cref="Release"/>: a command handler that runs
/// long and silent, as a job's body does. A host without it never waits.
/// </summary>
public sealed class AuditCommandGate
{
    private readonly TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits in the handler until the gate is released, after signalling that the handler is held.</summary>
    public Task HoldAsync()
    {
        this.held.TrySetResult();
        return this.released.Task;
    }

    /// <summary>Lets the held handler return.</summary>
    public void Release() => this.released.TrySetResult();

    /// <summary>Completes once the handler is held, or throws after ten seconds.</summary>
    public Task WaitUntilHeldAsync() => this.held.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
