using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

public sealed class SpyOutboundInterceptor : IOutboundInterceptor
{
    public List<DispatchEnvelope> Captured { get; } = [];
    public void Intercept(DispatchEnvelope envelope) => this.Captured.Add(envelope);
}
