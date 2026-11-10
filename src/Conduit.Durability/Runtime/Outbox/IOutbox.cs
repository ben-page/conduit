using Conduit.Durability.Configuration;
using Conduit.Durability.Persistence.Outbox;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// User-facing operations on the outbox entries whose retries ran out: find them, and hand them back to the relay.
/// The relay marks an entry Failed once <see cref="OutboxOptions.MaxRetries"/> attempts have failed; the row then
/// stays (until <see cref="OutboxOptions.FailedRetention"/>) as the only copy of a message that never reached its
/// listeners or broker. Retrying makes it pending again with its retry count reset, due at once.
/// </summary>
public interface IOutbox
{
    /// <summary>Lists failed entries, oldest first, optionally of one message type.</summary>
    public Task<FailedOutboxPage> QueryFailedAsync(FailedOutboxFilter filter, CancellationToken cancellationToken);

    /// <summary>Hands a failed entry back to the relay: pending, its retry count reset, due at once.</summary>
    /// <param name="id">The entry's <see cref="OutboxEntry.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>
    /// <see langword="true"/> when the entry had failed and is pending now; <see langword="false"/> when no entry has
    /// the id or it has not failed (a pending or sent entry is left alone).
    /// </returns>
    public Task<bool> RetryFailedAsync(long id, CancellationToken cancellationToken);

    /// <summary>Hands every failed entry of a message type back to the relay, as <see cref="RetryFailedAsync(long, CancellationToken)"/> does one.</summary>
    /// <param name="messageType">The full type name the entries carry (<see cref="OutboxEntry.Type"/>).</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>How many entries went back to the relay.</returns>
    public Task<int> RetryFailedAsync(string messageType, CancellationToken cancellationToken);
}
