using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Tests.Support;

public sealed class SpyOutboundInterceptor : IOutboundInterceptor
{
    public List<DispatchEnvelope> Captured { get; } = [];

    /// <summary>The value returned from <see cref="Intercept"/> — models capture (true) vs. decline (false).</summary>
    public bool CaptureResult { get; set; } = true;

    public bool Intercept(DispatchEnvelope envelope)
    {
        this.Captured.Add(envelope);
        return this.CaptureResult;
    }
}
