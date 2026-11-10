using Conduit.Audit.Runtime;
using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

/// <summary>
/// An <see cref="AuditBatch"/> listener that waits on the host's <see cref="AuditBatchGate"/>, when it has one, and
/// ignores its token, as a store write that runs to the end does.
/// </summary>
public sealed class GatedAuditBatchListener(IServiceProvider services) : IEventListener<AuditBatch>
{
    public Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
        => services.GetService(typeof(AuditBatchGate)) is AuditBatchGate gate
            ? gate.HoldAsync()
            : Task.CompletedTask;
}
