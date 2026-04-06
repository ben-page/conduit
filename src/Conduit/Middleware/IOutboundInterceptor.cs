using Conduit.Runtime;

namespace Conduit.Middleware;

/// <summary>
/// Intercepts outbound fire-and-forget messages before they are sent to transports.
/// Registered as scoped in DI. When present, the generated mediator calls
/// <see cref="Intercept"/> instead of sending directly to transports.
/// Used by the outbox to capture messages for reliable delivery.
/// </summary>
public interface IOutboundInterceptor
{
    /// <summary>
    /// Captures an outbound dispatch envelope. Called by the generated mediator
    /// for events and void routed commands when an interceptor is registered.
    /// </summary>
    /// <param name="envelope">The dispatch envelope containing type and payload.</param>
    public void Intercept(DispatchEnvelope envelope);
}
