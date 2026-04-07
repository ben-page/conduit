using Conduit.Middleware;
using Conduit.Runtime;

namespace Conduit.Outbox.Runtime;

/// <summary>
/// Scoped <see cref="IOutboundInterceptor"/> implementation that buffers
/// outbound dispatch envelopes for the OutboxBehavior pipeline behavior.
/// Registered as scoped — one instance per dispatch scope.
/// </summary>
public sealed class OutboxCapture : IOutboundInterceptor
{
    private readonly List<DispatchEnvelope> captured = [];

    /// <summary>The envelopes captured during this scope.</summary>
    public IReadOnlyList<DispatchEnvelope> Captured => this.captured;

    /// <inheritdoc />
    public void Intercept(DispatchEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        this.captured.Add(envelope);
    }
}
