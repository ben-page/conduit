using Conduit.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Conduit.Configuration;

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
    /// Logical system name used as the base for all broker resource names
    /// (topics, exchanges, queues). Default: "conduit".
    /// </summary>
    public string SystemName
    {
        get => this.Options.SystemName;
        set => this.Options.SystemName = value;
    }

    /// <summary>
    /// Application name used by transports for queue/topic naming.
    /// Defaults to IHostEnvironment.ApplicationName at transport startup if not set.
    /// </summary>
    public string? ApplicationName
    {
        get => this.Options.ApplicationName;
        set => this.Options.ApplicationName = value;
    }

    /// <summary>
    /// Controls resource naming and durability for broker transports.
    /// Production creates durable resources. Developer and Test create
    /// ephemeral auto-delete resources with prefixed names.
    /// </summary>
    public TopologyMode TopologyMode
    {
        get => this.Options.TopologyMode;
        set => this.Options.TopologyMode = value;
    }

    /// <summary>
    /// Prefix for broker resource names in Developer and Test topology modes.
    /// If not set, auto-resolves on first access: Developer defaults to
    /// <see cref="Environment.UserName"/>, Test generates a random prefix.
    /// Production mode always resolves to null.
    /// </summary>
    public string? TopologyPrefix
    {
        get => this.Options.TopologyPrefix;
        set => this.Options.TopologyPrefix = value;
    }

    /// <summary>
    /// When true, the transport only publishes messages — it does not create
    /// inbound topology (subscriptions, bindings) or start consumers.
    /// Reply queues for request/response are still created.
    /// Use for API hosts that route commands to worker services,
    /// or test sender hosts that should not interfere with handler topology.
    /// </summary>
    public bool ProducerOnly
    {
        get => this.Options.ProducerOnly;
        set => this.Options.ProducerOnly = value;
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
        var endpointBuilder = new EndpointBuilder(endpoint, this.Options, this.Services);
        configure(endpointBuilder);
        this.Options.Endpoints[name] = endpoint;
        return this;
    }

    /// <summary>
    /// Registers an open generic pipeline behavior. The type must implement
    /// <c>IPipelineBehavior&lt;TRequest, TResult&gt;</c>.
    /// Pipelines execute in registration order (first registered = outermost).
    /// The same pipeline wraps commands, queries, and events uniformly.
    /// </summary>
    /// <param name="behaviorType">An open generic type implementing IPipelineBehavior&lt;,&gt;.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitBuilder AddPipeline(Type behaviorType)
    {
        ArgumentNullException.ThrowIfNull(behaviorType);

        if (!behaviorType.GetInterfaces().Any(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IPipelineBehavior<,>)))
        {
            throw new ArgumentException(
                $"Type '{behaviorType.Name}' does not implement IPipelineBehavior<,>.",
                nameof(behaviorType));
        }

        this.Options.PipelineTypes.Add(behaviorType);
        this.Services.AddTransient(behaviorType);
        return this;
    }

    /// <summary>
    /// Adds a type filter interceptor that restricts which message types
    /// external clients are permitted to dispatch via transports.
    /// </summary>
    /// <param name="configure">Callback to configure allowed types.</param>
    /// <returns>This builder for chaining.</returns>
    public ConduitBuilder AddTypeFilter(Action<TypeFilterBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var filterBuilder = new TypeFilterBuilder();
        configure(filterBuilder);

        this.Services.Configure<TypeFilterOptions>(options =>
        {
            foreach (var type in filterBuilder.Options.AllowedTypes)
            {
                options.AllowedTypes.Add(type);
            }
        });

        this.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IInboundInterceptor, TypeFilterInterceptor>());
        return this;
    }
}
