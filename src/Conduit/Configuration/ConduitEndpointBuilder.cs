using Conduit.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Configuration;

/// <summary>
/// Configures a <see cref="ConduitEndpoint"/> during DI setup.
/// Transport-specific extension methods (e.g. UseSignalR, UseHttp) are provided
/// by transport packages like Conduit.Transport.Http and Conduit.Transport.SignalR.
/// </summary>
public sealed class ConduitEndpointBuilder
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
    /// This endpoint's configuration section (<c>Conduit:Endpoints:{EndpointName}</c>), or null
    /// when no configuration was supplied. Transport extensions bind their options from it.
    /// </summary>
    public IConfiguration? Configuration { get; }

    private ConduitOptions Options { get; }

    /// <summary>
    /// Creates a new builder for the given endpoint.
    /// </summary>
    internal ConduitEndpointBuilder(
        ConduitEndpoint endpoint,
        ConduitOptions options,
        IServiceCollection services,
        IConfiguration? configuration = null)
    {
        this.Endpoint = endpoint;
        this.Options = options;
        this.Services = services;
        this.Configuration = configuration?.GetSection($"Endpoints:{endpoint.Name}");
    }

    /// <summary>
    /// Registers a DI-managed transport for this endpoint. The concrete type must be
    /// pre-registered as a keyed singleton with this endpoint's name as the key.
    /// Typically called by transport extension methods after their own keyed registration; a broker
    /// transport's extension must call <see cref="UseTransport{TTransport}(BrokerTransportOptions)"/> instead.
    /// </summary>
    public ConduitEndpointBuilder UseTransport<TTransport>() where TTransport : class, IConduitTransport
    {
        this.Services.AddKeyedSingleton<IConduitTransport>(
            this.Endpoint.Name,
            (serviceProvider, key) => serviceProvider.GetRequiredKeyedService<TTransport>(key));
        return this;
    }

    /// <summary>
    /// Registers a DI-managed broker transport for this endpoint, as <see cref="UseTransport{TTransport}()"/>, once the
    /// settings every broker transport shares are valid, so a misconfigured endpoint fails here rather than at its first
    /// request. A broker transport's extension (any transport whose options derive from
    /// <see cref="BrokerTransportOptions"/>) must register through this overload, never through
    /// <see cref="UseTransport{TTransport}()"/>, which skips the validation; it calls it last, after configuration and
    /// its <c>configure</c> callback have set <paramref name="options"/>.
    /// </summary>
    /// <param name="options">This endpoint's transport options.</param>
    /// <exception cref="InvalidOperationException"><see cref="BrokerTransportOptions.ResponseTimeout"/> is not positive
    /// and finite.</exception>
    public ConduitEndpointBuilder UseTransport<TTransport>(BrokerTransportOptions options) where TTransport : class, IConduitTransport
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ThrowIfResponseTimeoutOutOfRange(this.EndpointName);
        return this.UseTransport<TTransport>();
    }

    /// <summary>
    /// Registers a transport instance for this endpoint. The instance is registered
    /// as a keyed singleton in DI. Useful for simple transports and test spies.
    /// </summary>
    public ConduitEndpointBuilder UseTransport(IConduitTransport transport)
    {
        this.Services.AddKeyedSingleton<IConduitTransport>(this.Endpoint.Name, transport);
        return this;
    }

    /// <summary>
    /// Routes all message types in the namespace of <typeparamref name="T"/>, and in the namespaces
    /// beneath it, to this endpoint. The match is on whole namespace segments: the namespace
    /// <c>Orders.Commands</c> does not cover <c>Orders.CommandsArchive</c>.
    /// </summary>
    /// <typeparam name="T">A type whose namespace defines the route pattern.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public ConduitEndpointBuilder RouteNamespace<T>()
    {
        var pattern = typeof(T).Namespace ?? typeof(T).FullName ?? typeof(T).Name;
        this.Options.Routes.Add(new ConduitRoute(pattern, this.EndpointName, Exact: false));
        return this;
    }

    /// <summary>
    /// Routes the specific message type <typeparamref name="T"/> to this endpoint. The match is
    /// exact: a type whose name merely starts with <typeparamref name="T"/>'s is not routed.
    /// </summary>
    /// <typeparam name="T">The message type to route.</typeparam>
    /// <returns>This builder for chaining.</returns>
    public ConduitEndpointBuilder RouteType<T>()
    {
        var pattern = typeof(T).FullName ?? typeof(T).Name;
        this.Options.Routes.Add(new ConduitRoute(pattern, this.EndpointName, Exact: true));
        return this;
    }
}
