using System.Collections.Concurrent;
using Conduit.Runtime;

namespace Conduit.Durability.Tests.Support;

/// <summary>
/// A transport whose first redelivery is held open until <see cref="Release"/>, so a test can run a second replay while
/// the first is mid-redelivery. Every redelivery is recorded as it starts; the ones after the first complete at once.
/// </summary>
internal sealed class GatedRedeliveryTransport : IConduitTransport
{
    private readonly TaskCompletionSource firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<DispatchEnvelope> redelivered = new();

    /// <summary>Every redelivery started so far.</summary>
    public IReadOnlyList<DispatchEnvelope> Redelivered => [.. this.redelivered];

    /// <summary>Completes when the first redelivery has started and is held.</summary>
    public Task FirstRedeliveryStarted => this.firstStarted.Task;

    /// <summary>Lets the held first redelivery complete.</summary>
    public void Release() => this.gate.TrySetResult();

    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken)
    {
        this.redelivered.Enqueue(envelope);
        return this.firstStarted.TrySetResult() ? this.gate.Task : Task.CompletedTask;
    }
}
