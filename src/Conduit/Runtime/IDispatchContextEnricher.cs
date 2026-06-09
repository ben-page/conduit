using System.Security.Claims;

namespace Conduit.Runtime;

/// <summary>
/// Contributes server-trusted properties to an inbound transport dispatch's
/// <see cref="MessageContext.Properties"/>, derived from the authenticated caller.
/// </summary>
/// <remarks>
/// Transports invoke registered enrichers server-side, after copying any
/// client-supplied headers but before calling
/// <see cref="MessageContext.StartDispatchFromTransport"/>. Enrichers run last,
/// so values they set overwrite client-supplied entries of the same key — a client
/// cannot spoof a server-trusted property (e.g. the authenticated user id).
/// Multiple registered enrichers compose; they run in registration order.
/// </remarks>
public interface IDispatchContextEnricher
{
    /// <summary>
    /// Adds or overwrites server-trusted entries on <paramref name="properties"/> for the
    /// current inbound dispatch.
    /// </summary>
    /// <param name="user">The authenticated caller, or <see langword="null"/> when the
    /// request is unauthenticated. Implementations should no-op when no trusted identity
    /// is available rather than throwing.</param>
    /// <param name="properties">The dispatch property bag, pre-seeded with client-supplied
    /// headers. Mutated in place; entries written here take precedence over client values.</param>
    public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties);
}
