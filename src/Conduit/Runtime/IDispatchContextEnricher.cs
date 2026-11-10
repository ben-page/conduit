using System.Security.Claims;

namespace Conduit.Runtime;

/// <summary>
/// Contributes server-trusted properties to an inbound transport dispatch's
/// <see cref="MessageContext.Properties"/>, derived from the authenticated caller.
/// </summary>
/// <remarks>
/// Transports invoke registered enrichers server-side, after copying any
/// client-supplied headers (the hub's <c>headers</c> argument; the HTTP wire endpoint's
/// <c>X-Conduit-Property-*</c> headers when it is mapped with <c>readClientProperties: true</c>) but before calling
/// <see cref="MessageContext.StartDispatchFromTransport"/>. Enrichers run last,
/// so a value they write overwrites a client-supplied entry of the same key.
/// <para>
/// <b>An enricher that owns a key must write it or remove it on every dispatch</b>, unauthenticated ones
/// included: a dispatch it leaves alone keeps whatever the client sent under that key. With no identity to derive
/// the value from, remove the keys you own (<c>properties.Remove("UserId")</c>); never just return. Property names
/// are case-insensitive, so one removal reaches every spelling a client sent.
/// </para>
/// Multiple registered enrichers compose; they run in registration order.
/// </remarks>
public interface IDispatchContextEnricher
{
    /// <summary>
    /// Adds, overwrites or removes server-trusted entries on <paramref name="properties"/> for the
    /// current inbound dispatch.
    /// </summary>
    /// <param name="user">The authenticated caller, or <see langword="null"/> when the
    /// request is unauthenticated. With no trusted identity, remove the keys you own rather than throwing: a no-op
    /// leaves the client's value of each in place.</param>
    /// <param name="properties">The dispatch property bag, pre-seeded with client-supplied
    /// headers, its names case-insensitive. Mutated in place; entries written here take precedence over client values,
    /// and an entry removed here is gone.</param>
    public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties);
}
