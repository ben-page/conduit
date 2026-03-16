using Conduit.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Configuration;

/// <summary>
/// Configures a <see cref="ConduitEndpoint"/> during DI setup.
/// Transport-specific extension methods (e.g. UseSignalR, UseHttp) are provided
/// by transport packages like Conduit.Transport.Http and Conduit.Transport.SignalR.
/// </summary>
public sealed class EndpointBuilder
{
    /// <summary>
    /// The endpoint being configured.
    /// </summary>
    internal ConduitEndpoint Endpoint { get; }

    /// <summary>
    /// The service collection, for transport extensions that need to register DI services.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Creates a new builder for the given endpoint.
    /// </summary>
    internal EndpointBuilder(ConduitEndpoint endpoint, IServiceCollection services)
    {
        this.Endpoint = endpoint;
        this.Services = services;
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
