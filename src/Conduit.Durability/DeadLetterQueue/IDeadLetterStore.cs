namespace Conduit.Durability.DeadLetterQueue;

/// <summary>
/// Persistence abstraction for the dead letter queue.
/// Implementations: <c>DbDeadLetterStore</c> (database-backed) or per-transport broker-native stores.
/// </summary>
public interface IDeadLetterStore
{
    /// <summary>Capture a failed message into the dead letter store.</summary>
    public Task WriteAsync(DeadLetterEntry entry, CancellationToken cancellationToken);

    /// <summary>Get a single entry by ID. Returns null if not found.</summary>
    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Non-destructive browse with optional filtering and pagination.</summary>
    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Claims an entry for one replay, in a single atomic step that succeeds only when no other replay holds it, so two
    /// replays of one entry (two operators, two hosts) cannot both redeliver it. The replay then redelivers the entry and
    /// removes it, or releases the claim when the redelivery fails (<see cref="ReleaseReplayClaimAsync"/>). A claim a
    /// replay never settled, its process having stopped between the claim and the removal, is abandoned once it is older
    /// than the store's claim timeout, and the entry can then be claimed again.
    /// </summary>
    /// <param name="id">The entry to claim.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The claim's token, or <see langword="null"/> when there is no such entry or another replay holds it.</returns>
    public Task<string?> ClaimForReplayAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Releases a replay's claim on an entry whose redelivery failed, so the next replay can claim it at once. Does
    /// nothing when the entry is no longer held under <paramref name="claimToken"/> (its claim was abandoned and taken over).
    /// </summary>
    /// <param name="id">The claimed entry.</param>
    /// <param name="claimToken">The token <see cref="ClaimForReplayAsync"/> returned.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public Task ReleaseReplayClaimAsync(Guid id, string claimToken, CancellationToken cancellationToken);

    /// <summary>Remove a single entry from the store.</summary>
    public Task RemoveAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Remove multiple entries from the store.</summary>
    public Task RemoveAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Create the backing table/resources if they don't exist.</summary>
    public Task EnsureTableExistsAsync(CancellationToken cancellationToken);
}
