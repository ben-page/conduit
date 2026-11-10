using Conduit.Audit.Runtime;
using Conduit.Handlers;

namespace Conduit.Audit.Tests.Support;

/// <summary>Hands each published <see cref="AuditBatch"/> to the host's <see cref="PublishedAuditBatches"/>, when it has one.</summary>
public sealed class PublishedAuditBatchListener(IServiceProvider services) : IEventListener<AuditBatch>
{
    public Task HandleAsync(AuditBatch batch, CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(PublishedAuditBatches)) is PublishedAuditBatches published)
        {
            published.Add(batch);
        }

        return Task.CompletedTask;
    }
}
