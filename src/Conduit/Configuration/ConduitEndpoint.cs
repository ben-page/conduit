using Conduit.Routing;

namespace Conduit.Configuration;

/// <summary>
/// Represents a named remote destination that messages can be routed to.
/// Each endpoint has a transport that handles the protocol-specific communication.
/// </summary>
public sealed class ConduitEndpoint
{
    /// <summary>
    /// The unique name of this endpoint (e.g. "orders-api", "frontend").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The transport used to communicate with this endpoint.
    /// Set during endpoint configuration via <see cref="EndpointBuilder"/>.
    /// </summary>
    public IConduitTransport? Transport { get; set; }
}
