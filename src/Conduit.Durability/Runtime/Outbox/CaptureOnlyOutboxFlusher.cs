using Conduit.Durability.Outbox;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>The <see cref="IOutboxFlusher"/> of a capture-only outbox (<see cref="Configuration.OutboxOptions.CaptureOnly"/>):
/// delivery belongs to another process's relay, so flushing schedules nothing.</summary>
public sealed class CaptureOnlyOutboxFlusher : IOutboxFlusher
{
    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
