using Conduit.Runtime;

namespace Conduit.Middleware;

/// <summary>
/// Intercepts outbound fire-and-forget messages before they are sent to transports.
/// Registered as scoped in DI. When present, the generated mediator offers each capturable
/// envelope to <see cref="Intercept"/> before any local enqueue or transport send; the return
/// value tells the mediator whether the interceptor took ownership of delivery.
/// Used by the outbox to capture messages for reliable, transaction-gated delivery.
/// </summary>
public interface IOutboundInterceptor
{
    /// <summary>
    /// Attempts to capture an outbound dispatch envelope. Called by the generated mediator for
    /// events and routed void commands when an interceptor is registered, BEFORE any local
    /// enqueue or transport send.
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing type and payload.</param>
    /// <returns>
    /// <see langword="true"/> when the envelope was captured — the caller must skip local
    /// enqueue and transport send (the outbox delivers post-commit). <see langword="false"/>
    /// when capture is declined (no ambient transaction) — the caller proceeds exactly as if
    /// no interceptor were registered.
    /// </returns>
    public bool Intercept(DispatchEnvelope envelope);
}
