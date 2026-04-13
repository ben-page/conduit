using Conduit.Runtime;
using Microsoft.Extensions.Hosting;

namespace Conduit.Audit.Runtime;

/// <summary>
/// Hosted service that wires the <see cref="AuditObserver"/> into the dispatch pipeline
/// at startup and removes it at shutdown.
/// </summary>
internal sealed class AuditObserverInitializer(AuditObserver observer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        MessageContext.Observer = observer;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        MessageContext.Observer = null;
        return Task.CompletedTask;
    }
}
