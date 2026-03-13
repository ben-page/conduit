namespace Conduit.Routing;

/// <summary>
/// Configures a <see cref="ConduitEndpoint"/> during DI setup.
/// Transport-specific extension methods (e.g. UseSignalR, UseHttp) are provided
/// by transport packages like Conduit.AspNetCore.
/// </summary>
public sealed class EndpointBuilder
{
    /// <summary>
    /// The endpoint being configured.
    /// </summary>
    internal ConduitEndpoint Endpoint { get; }

    /// <summary>
    /// Creates a new builder for the given endpoint.
    /// </summary>
    internal EndpointBuilder(ConduitEndpoint endpoint)
    {
        this.Endpoint = endpoint;
    }

    /// <summary>
    /// Sets the transport for this endpoint.
    /// Typically called by transport-specific extension methods rather than directly.
    /// </summary>
    public EndpointBuilder UseTransport(IConduitTransport transport)
    {
        this.Endpoint.Transport = transport;
        return this;
    }
}
