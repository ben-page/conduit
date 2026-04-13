using Conduit.Audit.Runtime;
using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

/// <summary>
/// Stub listener that enables the source generator to discover that <see cref="AuditBatch"/>
/// has local listeners. Without this, the generated <c>PublishLocalAsync</c> has no case arm
/// for <c>AuditBatch</c> and DI-registered listeners (like <see cref="LocalAuditListener"/>)
/// are never invoked. This stub is a no-op — the real listener is registered by
/// <c>UseLocalStore</c> at runtime.
/// </summary>
public sealed class AuditBatchListenerStub : IEventListener<AuditBatch>
{
    public Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
