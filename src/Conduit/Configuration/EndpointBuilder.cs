using Conduit.Runtime;
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
    /// The name of the endpoint being configured. Used by transport extensions
    /// to register keyed services for this endpoint.
    /// </summary>
    public string EndpointName => this.Endpoint.Name;

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
    /// Registers a DI-managed transport for this endpoint. The concrete type must be
    /// pre-registered as a keyed singleton with this endpoint's name as the key.
    /// Typically called by transport extension methods after their own keyed registration.
    /// </summary>
    public EndpointBuilder UseTransport<TTransport>() where TTransport : class, IConduitTransport
    {
        this.Services.AddKeyedSingleton<IConduitTransport>(
            this.Endpoint.Name,
            (serviceProvider, key) => serviceProvider.GetRequiredKeyedService<TTransport>(key));
        return this;
    }

    /// <summary>
    /// Registers a transport instance for this endpoint. The instance is registered
    /// as a keyed singleton in DI. Useful for simple transports and test spies.
    /// </summary>
    public EndpointBuilder UseTransport(IConduitTransport transport)
    {
        this.Services.AddKeyedSingleton<IConduitTransport>(this.Endpoint.Name, transport);
        return this;
    }
}
