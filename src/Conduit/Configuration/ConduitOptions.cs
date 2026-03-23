namespace Conduit.Configuration;

/// <summary>
/// Configuration for Conduit message routing, holding endpoint definitions
/// and route mappings. Populated by <see cref="ConduitBuilder"/> during DI setup.
/// </summary>
public sealed class ConduitOptions
{
    private Lazy<string?>? topologyPrefix;

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
    /// Application name used by transports for queue/topic naming.
    /// Defaults to IHostEnvironment.ApplicationName at transport startup if not set.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Open generic pipeline behavior types in registration order
    /// (first = outermost). Each type must implement
    /// <c>IPipelineBehavior&lt;,&gt;</c>. Applied to commands, queries,
    /// and events uniformly.
    /// </summary>
    public List<Type> PipelineTypes { get; } = [];

    public string SystemName { get; set; } = "conduit";
    public TopologyMode TopologyMode { get; set; } = TopologyMode.Production;
    public string? DeveloperPrefix { get; set; }

    /// <summary>
    /// Optional prefix for Test topology mode. If set, all hosts sharing
    /// this prefix will use the same broker resources (exchanges, queues, topics).
    /// If null, a random prefix is generated per options instance.
    /// </summary>
    public string? TestPrefix { get; set; }

    /// <summary>
    /// When true, the transport only publishes messages — it does not create
    /// inbound topology (subscriptions, bindings) or start consumers.
    /// Reply queues for request/response are still created.
    /// Use for API hosts that route commands to worker services,
    /// or test sender hosts that should not interfere with handler topology.
    /// </summary>
    public bool ProducerOnly { get; set; }

    /// <summary>
    /// Computed topology prefix. Resolved once on first access using the current
    /// TopologyMode and DeveloperPrefix/TestPrefix values. Test mode generates a stable
    /// random prefix per options instance if TestPrefix is not set.
    /// </summary>
    internal string? TopologyPrefix =>
        (this.topologyPrefix ??= new Lazy<string?>(this.ComputeTopologyPrefix)).Value;

    private string? ComputeTopologyPrefix() => this.TopologyMode switch
    {
        TopologyMode.Developer => this.DeveloperPrefix ?? Environment.UserName,
        TopologyMode.Test => this.TestPrefix ?? $"test-{Guid.NewGuid():N}"[..13],
        TopologyMode.Production => null,
        _ => null,
    };
}
