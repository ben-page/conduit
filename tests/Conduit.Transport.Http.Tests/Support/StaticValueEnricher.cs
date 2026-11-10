using System.Security.Claims;
using Conduit.Runtime;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Test enricher that unconditionally writes a fixed key/value, proving the transport invokes
/// enrichers on a request and that their writes reach the dispatch's properties, over a client property of the same
/// name (an <c>X-Conduit-Property-*</c> header on the wire endpoint) too.
/// </summary>
public sealed class StaticValueEnricher(string key, object? value) : IDispatchContextEnricher
{
    public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties)
    {
        properties[key] = value;
    }
}
