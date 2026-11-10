namespace Conduit.Durability.DeadLetterQueue;

/// <summary>
/// User-facing dead letter operations: browse, replay, and discard.
/// Replay re-publishes messages to the broker via the source transport's redeliver method.
/// </summary>
public interface IDeadLetters
{
    /// <summary>Browse dead-lettered messages with optional filtering.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="DeadLetterFilter.PageSize"/> is not positive or is above <see cref="DeadLetterFilter.MaxPageSize"/>.
    /// </exception>
    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken);

    /// <summary>Get a single dead letter entry by ID.</summary>
    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Replay a single message: re-publish to the broker via the source transport, then remove the entry. The entry is
    /// claimed first, so a replay of an entry another replay holds fails instead of re-publishing it twice; a failed
    /// re-publish frees it for the next replay. At least once: a process that stops between the re-publish and the
    /// removal leaves the entry claimed, and a replay after the claim times out (five minutes) re-publishes it again.
    /// </summary>
    public Task<ReplayResult> ReplayAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Replay multiple messages sequentially.</summary>
    public Task<IReadOnlyList<ReplayResult>> ReplayAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// Replay all dead-lettered messages of a given type: every one the store holds when the call reads them, however
    /// many pages they fill.
    /// </summary>
    public Task<IReadOnlyList<ReplayResult>> ReplayByTypeAsync(string messageType, CancellationToken cancellationToken);

    /// <summary>Discard a message (remove from store without replaying).</summary>
    public Task DiscardAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Discard multiple messages.</summary>
    public Task DiscardAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
}
