namespace Conduit.Bridge.SignalR;

/// <summary>
/// Configuration options for the SignalR bridge, controlling which message types
/// are allowed through from browser clients.
/// </summary>
public sealed class BridgeOptions
{
    /// <summary>
    /// The set of message type names that browser clients are permitted to dispatch.
    /// Types not in this set are rejected by the <see cref="BridgeTypeFilterInterceptor"/>.
    /// </summary>
    public HashSet<string> AllowedTypes { get; } = [];
}
