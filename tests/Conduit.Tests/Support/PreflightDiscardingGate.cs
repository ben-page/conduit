using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>An inbound gate that discards every request, as an inbox gate discards a duplicate.</summary>
public sealed class PreflightDiscardingGate : IInboundInterceptor
{
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken) => Task.FromResult(false);
}
