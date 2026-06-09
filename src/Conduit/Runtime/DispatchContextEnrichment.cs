using System.Security.Claims;

namespace Conduit.Runtime;

/// <summary>
/// Transport helper that composes registered <see cref="IDispatchContextEnricher"/>
/// instances over a set of client-supplied dispatch properties.
/// </summary>
/// <remarks>
/// Used by inbound transports (SignalR hub, HTTP endpoint) to fold server-trusted
/// values onto the property bag before <see cref="MessageContext.StartDispatchFromTransport"/>.
/// Enrichers run after the client headers are seeded, so their writes win over any
/// client-supplied entry of the same key.
/// </remarks>
public static class DispatchContextEnrichment
{
    /// <summary>
    /// Applies <paramref name="enrichers"/> over <paramref name="clientProperties"/>, returning the
    /// merged property bag, or <see langword="null"/> when there is nothing to contribute.
    /// </summary>
    /// <param name="enrichers">The registered enrichers. May be empty.</param>
    /// <param name="user">The authenticated caller, or <see langword="null"/> when unauthenticated.</param>
    /// <param name="clientProperties">Properties derived from client-supplied transport headers,
    /// or <see langword="null"/> when none were supplied.</param>
    /// <returns>The merged properties, or <see langword="null"/> when no enrichers contributed and
    /// no client properties were supplied — preserving backward-compatible behavior.</returns>
    public static IReadOnlyDictionary<string, object?>? Apply(
        IEnumerable<IDispatchContextEnricher> enrichers,
        ClaimsPrincipal? user,
        IReadOnlyDictionary<string, object?>? clientProperties)
    {
        ArgumentNullException.ThrowIfNull(enrichers);

        // Materialize the enrichers once so we can short-circuit when there are none.
        var enricherList = enrichers as IReadOnlyList<IDispatchContextEnricher> ?? enrichers.ToList();
        if (enricherList.Count == 0)
        {
            return clientProperties;
        }

        var merged = clientProperties is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(clientProperties);

        // Enrichers run after the client headers are copied, so their writes overwrite
        // any client-supplied value of the same key (anti-spoofing).
        foreach (var enricher in enricherList)
        {
            enricher.Enrich(user, merged);
        }

        return merged.Count == 0 ? clientProperties : merged;
    }
}
