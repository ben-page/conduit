namespace Conduit.Routing;

/// <summary>
/// Maps a message type namespace pattern to a named endpoint.
/// Messages matching the pattern will be routed to the endpoint's transport
/// instead of being dispatched locally.
/// </summary>
public sealed class ConduitRoute
{
    /// <summary>
    /// The namespace prefix to match against message type full names.
    /// For example, "Orders.Commands" matches "Orders.Commands.CreateOrder".
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>
    /// The name of the endpoint to route matching messages to.
    /// Must correspond to an endpoint registered via <see cref="ConduitBuilder.AddEndpoint"/>.
    /// </summary>
    public required string EndpointName { get; init; }
}
