using Conduit.Durability.Persistence.Outbox;

namespace Conduit.Durability.Runtime.Outbox;

/// <summary>
/// The <see cref="IOutbox"/> that <c>AddOutbox</c> registers, over the registered <see cref="IOutboxStore"/>.
/// </summary>
public sealed class OutboxOperations : IOutbox
{
    private readonly IOutboxStore store;

    /// <summary>Initializes a new instance of <see cref="OutboxOperations"/>.</summary>
    /// <param name="store">The outbox store holding the entries.</param>
    public OutboxOperations(IOutboxStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        this.store = store;
    }

    /// <inheritdoc/>
    public Task<FailedOutboxPage> QueryFailedAsync(FailedOutboxFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(filter.PageSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(filter.PageSize, FailedOutboxFilter.MaxPageSize);

        return this.store.QueryFailedAsync(filter, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<bool> RetryFailedAsync(long id, CancellationToken cancellationToken)
        => this.store.RetryFailedAsync(id, cancellationToken);

    /// <inheritdoc/>
    public Task<int> RetryFailedAsync(string messageType, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);

        return this.store.RetryFailedAsync(messageType, cancellationToken);
    }
}
