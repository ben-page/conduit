using Conduit.Middleware;

namespace Conduit.Tests.Support;

/// <summary>
/// An inbound gate that throws something other than a refusal, as an inbox whose store is unreachable does: a failure
/// to decide, not a verdict on the message.
/// </summary>
public sealed class ThrowingGate : IInboundInterceptor
{
    public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The inbox store is unreachable.");
}
