namespace Conduit.Configuration;

/// <summary>
/// Controls resource naming and durability for broker transport topology.
/// </summary>
public enum TopologyMode
{
    /// <summary>Durable resources with no auto-delete. For production deployments.</summary>
    Production,

    /// <summary>Resources auto-delete after 4 hours. Prefixed with the current user name.</summary>
    Developer,

    /// <summary>Resources auto-delete after 5 minutes. Prefixed with a random 13-char string.</summary>
    Test,
}
