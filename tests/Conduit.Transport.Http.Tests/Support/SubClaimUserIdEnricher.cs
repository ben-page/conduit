using System.Security.Claims;
using Conduit.Runtime;

namespace Conduit.Transport.Http.Tests.Support;

/// <summary>
/// Test enricher that projects the authenticated caller's <c>sub</c> claim into a
/// server-trusted <c>UserId</c> property, proving the HTTP transport passes
/// <c>HttpContext.User</c> through to enrichers. It owns <c>UserId</c>, so with no <c>sub</c> claim it removes the
/// key: a client's <c>UserId</c> property must never survive an anonymous request.
/// </summary>
public sealed class SubClaimUserIdEnricher : IDispatchContextEnricher
{
    public void Enrich(ClaimsPrincipal? user, IDictionary<string, object?> properties)
    {
        var sub = user?.FindFirst("sub")?.Value;
        if (sub is not null)
        {
            properties["UserId"] = sub;
        }
        else
        {
            properties.Remove("UserId");
        }
    }
}
