namespace Conduit.Durability.DeadLetterQueue;

/// <summary>
/// User-facing dead letter operations: browse, replay, and discard.
/// Replay re-publishes messages to the broker via the source transport's redeliver method.
/// </summary>
public interface IDeadLetters
{
    /// <summary>Browse dead-lettered messages with optional filtering.</summary>
    public Task<DeadLetterPage> QueryAsync(DeadLetterFilter filter, CancellationToken cancellationToken);

    /// <summary>Get a single dead letter entry by ID.</summary>
    public Task<DeadLetterEntry?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Replay a single message: re-publish to the broker via the source transport.</summary>
    public Task<ReplayResult> ReplayAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Replay multiple messages sequentially.</summary>
    public Task<IReadOnlyList<ReplayResult>> ReplayAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Replay all dead-lettered messages of a given type.</summary>
    public Task<IReadOnlyList<ReplayResult>> ReplayByTypeAsync(string messageType, CancellationToken cancellationToken);

    /// <summary>Discard a message (remove from store without replaying).</summary>
    public Task DiscardAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Discard multiple messages.</summary>
    public Task DiscardAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken);
}
