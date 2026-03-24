namespace Conduit.Configuration;

/// <summary>
/// Configuration for Conduit message routing, holding endpoint definitions
/// and route mappings. Populated by <see cref="ConduitBuilder"/> during DI setup.
/// </summary>
public sealed class ConduitOptions
{
    private Lazy<string?>? resolvedTopologyPrefix;

    /// <summary>
    /// Logical system name used as the base for all broker resource names
    /// (topics, exchanges, queues). Default: "conduit".
    /// </summary>
    public string SystemName { get; set; } = "conduit";

    /// <summary>
    /// Application name used by transports for queue/topic naming.
    /// Defaults to IHostEnvironment.ApplicationName at transport startup if not set.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Controls resource naming and durability for broker transports.
    /// Production creates durable resources. Developer and Test create
    /// ephemeral auto-delete resources with prefixed names.
    /// </summary>
    public TopologyMode TopologyMode { get; set; } = TopologyMode.Production;

    /// <summary>
    /// Prefix for broker resource names in Developer and Test topology modes.
    /// If not set, auto-resolves on first access: Developer defaults to
    /// <see cref="Environment.UserName"/>, Test generates a random 13-character
    /// prefix. Production mode always resolves to null.
    /// Once resolved (by explicit set or auto-resolve), the value is stable.
    /// </summary>
    public string? TopologyPrefix
    {
        get => (this.resolvedTopologyPrefix ??= new Lazy<string?>(this.ResolveTopologyPrefix)).Value;

        set => this.resolvedTopologyPrefix = new Lazy<string?>(value);
    }

    /// <summary>
    /// When true, the transport only publishes messages — it does not create
    /// inbound topology (subscriptions, bindings) or start consumers.
    /// Reply queues for request/response are still created.
    /// Use for API hosts that route commands to worker services,
    /// or test sender hosts that should not interfere with handler topology.
    /// </summary>
    public bool ProducerOnly { get; set; }

    /// <summary>
    /// Named endpoints that messages can be routed to.
    /// Key is the endpoint name.
    /// </summary>
    public Dictionary<string, ConduitEndpoint> Endpoints { get; } = new();

    /// <summary>
    /// Route mappings from message namespace patterns to endpoint names.
    /// Evaluated in order; first match wins.
    /// </summary>
    public List<ConduitRoute> Routes { get; } = [];

    /// <summary>
    /// Open generic pipeline behavior types in registration order
    /// (first = outermost). Each type must implement
    /// <c>IPipelineBehavior&lt;,&gt;</c>. Applied to commands, queries,
    /// and events uniformly.
    /// </summary>
    public List<Type> PipelineTypes { get; } = [];

    private string? ResolveTopologyPrefix() => this.TopologyMode switch
    {
        TopologyMode.Production => null,
        TopologyMode.Developer => Environment.UserName,
        TopologyMode.Test => $"test-{Guid.NewGuid():N}"[..13],
        _ => null,
    };
}
