using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Routing;

/// <summary>
/// Fluent builder for configuring Conduit routing, endpoints, and transports.
/// </summary>
public class ConduitBuilder
{
    /// <summary>
    /// The underlying service collection.
    /// </summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// The routing options being configured.
    /// </summary>
    public ConduitOptions Options { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ConduitBuilder"/> class.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    public ConduitBuilder(IServiceCollection services)
    {
        this.Services = services;
    }

    /// <summary>
    /// Adds a named remote endpoint that messages can be routed to.
    /// Configure the endpoint's transport using extension methods on <see cref="EndpointBuilder"/>.
    /// </summary>
    /// <param name="name">A unique name for this endpoint (e.g. "orders-api", "frontend").</param>
    /// <param name="configure">Callback to configure the endpoint's transport.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitBuilder AddEndpoint(string name, Action<EndpointBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var endpoint = new ConduitEndpoint { Name = name };
        var endpointBuilder = new EndpointBuilder(endpoint);
        configure(endpointBuilder);
        this.Options.Endpoints[name] = endpoint;
        return this;
    }

    /// <summary>
    /// Starts a route definition for messages in the given namespace.
    /// The namespace of <typeparamref name="T"/> is used as the route pattern.
    /// </summary>
    /// <typeparam name="T">A type whose namespace defines the route pattern.</typeparam>
    /// <returns>A builder to specify the target endpoint.</returns>
    public RouteBuilder Route<T>()
    {
        var pattern = typeof(T).Namespace ?? typeof(T).FullName ?? typeof(T).Name;
        return new RouteBuilder(this.Options, pattern);
    }

    /// <summary>
    /// Starts a route definition for messages matching the given namespace pattern.
    /// </summary>
    /// <param name="pattern">The namespace prefix to match (e.g. "Orders.Commands").</param>
    /// <returns>A builder to specify the target endpoint.</returns>
    public RouteBuilder Route(string pattern)
    {
        return new RouteBuilder(this.Options, pattern);
    }
}
