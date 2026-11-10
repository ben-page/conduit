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
/// client-supplied entry of the same key. Property names are case-insensitive
/// (<see cref="StringComparer.OrdinalIgnoreCase"/>): the bag an enricher receives matches <c>"userid"</c> to a
/// client's <c>"UserId"</c>, so removing or overwriting a key reaches every spelling of it.
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

        // Case-insensitive, and filled by the indexer: two client keys that differ only by case are one property (the
        // last one wins) rather than a duplicate-key exception.
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (clientProperties is not null)
        {
            foreach (var (key, value) in clientProperties)
            {
                merged[key] = value;
            }
        }

        // Enrichers run after the client headers are copied, so their writes overwrite
        // any client-supplied value of the same key (anti-spoofing).
        foreach (var enricher in enricherList)
        {
            enricher.Enrich(user, merged);
        }

        // Once enrichers have run, the merged bag is authoritative — including removals. We must NOT
        // fall back to the original clientProperties when merged ends up empty: an enricher may have
        // deliberately removed a client-supplied key (e.g. stripping a spoofed UserId it could not
        // re-derive from the principal), and returning clientProperties would resurrect exactly that
        // value, defeating the anti-spoof contract. Empty means "nothing trusted to carry" -> null.
        return merged.Count == 0 ? null : merged;
    }
}
