namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// Writes the envelopes captured in this scope that no <see cref="OutboxBehavior{TRequest,TResult}"/>
/// has written yet, through the current transaction. For a host's own transactional behavior that
/// publishes AFTER its <c>next()</c> returns (e.g. dispatching domain events before the commit):
/// those publishes are captured, but every <see cref="OutboxBehavior{TRequest,TResult}"/> in the scope
/// has already exited, so nothing else writes them. Call it after the last publish and before the
/// commit; then commit, then <see cref="IOutboxFlusher.FlushAsync"/>.
/// </summary>
public interface IOutboxPendingWriter
{
    /// <summary>
    /// Writes every captured-but-unwritten envelope of this scope to the outbox through the current
    /// transaction — the same entry shape and correlation as <see cref="OutboxBehavior{TRequest,TResult}"/>
    /// — and adds them to the messages <see cref="IOutboxFlusher.FlushAsync"/> delivers after the commit.
    /// A no-op when nothing is unwritten or no transaction is active (the envelopes stay pending).
    /// </summary>
    public Task WritePendingAsync(CancellationToken cancellationToken = default);
}
