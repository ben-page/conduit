namespace Conduit.Configuration;

/// <summary>
/// Configuration for Conduit message routing, holding endpoint definitions
/// and route mappings. Populated by <see cref="ConduitBuilder"/> during DI setup.
/// </summary>
public sealed class ConduitOptions
{
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
}
