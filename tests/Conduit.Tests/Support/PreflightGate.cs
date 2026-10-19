using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

/// <summary>An inbound gate that records the context it ran under (an inbox gate records that context's MessageId) and
/// admits.</summary>
public sealed class PreflightGate(PreflightProbe probe) : IInboundInterceptor
{
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
    {
        probe.SeeGate(MessageContext.Current);
        return Task.FromResult(true);
    }
}
