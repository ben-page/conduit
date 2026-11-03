using System.Security.Claims;
using Conduit.Runtime;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Test enricher that unconditionally writes a fixed key/value, proving the transport invokes
/// enrichers and that their writes overwrite client-supplied header values of the same key.
/// </summary>
public sealed class StaticValueEnricher(string key, object? value) : IDispatchContextEnricher
{
    public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties)
    {
        properties[key] = value;
    }
}
