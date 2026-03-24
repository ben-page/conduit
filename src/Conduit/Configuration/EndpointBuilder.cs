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

    private ConduitOptions Options { get; }

    /// <summary>
    /// Creates a new builder for the given endpoint.
    /// </summary>
    internal EndpointBuilder(ConduitEndpoint endpoint, ConduitOptions options, IServiceCollection services)
    {
        this.Endpoint = endpoint;
        this.Options = options;
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

    /// <summary>
    /// Routes all message types in the namespace of <typeparamref name="T"/> to this endpoint.
    /// </summary>
    /// <typeparam name="T">A type whose namespace defines the route pattern.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EndpointBuilder RouteNamespace<T>()
    {
        var pattern = typeof(T).Namespace ?? typeof(T).FullName ?? typeof(T).Name;
        this.Options.Routes.Add(new ConduitRoute { Pattern = pattern, EndpointName = this.EndpointName });
        return this;
    }

    /// <summary>
    /// Routes the specific message type <typeparamref name="T"/> to this endpoint.
    /// </summary>
    /// <typeparam name="T">The message type to route.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public EndpointBuilder RouteType<T>()
    {
        var pattern = typeof(T).FullName ?? typeof(T).Name;
        this.Options.Routes.Add(new ConduitRoute { Pattern = pattern, EndpointName = this.EndpointName });
        return this;
    }
}
