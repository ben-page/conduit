namespace Conduit.Configuration;

/// <summary>
/// Represents a named remote destination that messages can be routed to.
/// The transport for this endpoint is registered as a keyed service in DI
/// and resolved at dispatch time via <see cref="Conduit.Runtime.ConduitRuntime.ResolveTransport"/>.
/// </summary>
public sealed class ConduitEndpoint
{
    /// <summary>
    /// The unique name of this endpoint (e.g. "orders-api", "frontend").
    /// Also used as the DI keyed service key for resolving the transport.
    /// </summary>
    public required string Name { get; init; }
}
