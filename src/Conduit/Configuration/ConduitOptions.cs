namespace Conduit.Configuration;

/// <summary>
/// Configuration for Conduit message routing, holding endpoint definitions
/// and route mappings. Populated by <see cref="ConduitBuilder"/> during DI setup.
/// </summary>
public sealed class ConduitOptions
{
    private readonly Lazy<string?> topologyPrefix;

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

    public ConduitOptions()
    {
        this.topologyPrefix = new Lazy<string?>(() => this.TopologyMode switch
        {
            TopologyMode.Developer => this.DeveloperPrefix ?? Environment.UserName,
            TopologyMode.Test => $"test-{Guid.NewGuid():N}"[..13],
            TopologyMode.Production => null,
            _ => null,
        });
    }

    internal string? TopologyPrefix => this.topologyPrefix.Value;
}
