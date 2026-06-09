using Conduit.Attributes;
using Conduit.Audit.Persistence;
using Conduit.Handlers;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Event listener that persists <see cref="AuditBatch"/> records to the <see cref="IAuditStore"/>.
/// Registered by <c>AddAuditListener</c>, whether the batches are published in this process
/// or arrive over a transport from other services.
/// </summary>
/// <remarks>
/// Marked <see cref="NotAutoRegisteredAttribute"/> so the source generator does not auto-register it
/// across referenced assemblies — it depends on <see cref="IAuditStore"/>, which only
/// <c>AddAuditListener</c> registers alongside it.
/// </remarks>
[NotAutoRegistered]
public sealed class LocalAuditListener(IAuditStore store) : IEventListener<AuditBatch>
{
    /// <inheritdoc/>
    public async Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await store.SaveBatchAsync(batch.Records, cancellationToken);
    }
}
