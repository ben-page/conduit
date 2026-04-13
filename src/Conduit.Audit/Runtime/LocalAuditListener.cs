using Conduit.Audit.Persistence;
using Conduit.Handlers;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Event listener that persists <see cref="AuditBatch"/> records to the local <see cref="IAuditStore"/>.
/// Registered when the host is configured as an audit consumer via <c>AddAuditConsumer</c>
/// or when a local store is attached via <c>UseLocalStore</c>.
/// </summary>
public sealed class LocalAuditListener(IAuditStore store) : IEventListener<AuditBatch>
{
    /// <inheritdoc/>
    public async Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await store.SaveBatchAsync(batch.Records, cancellationToken);
    }
}
