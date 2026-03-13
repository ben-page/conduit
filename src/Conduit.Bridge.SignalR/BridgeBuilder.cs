namespace Conduit.Bridge.SignalR;

/// <summary>
/// Fluent builder for configuring the SignalR bridge.
/// </summary>
public sealed class BridgeBuilder
{
    internal BridgeOptions Options { get; } = new();

    /// <summary>
    /// Allows the specified message type name to be dispatched through the bridge.
    /// </summary>
    /// <param name="typeName">The short type name (e.g. "CreateOrder").</param>
    /// <returns>This builder for chaining.</returns>
    public BridgeBuilder AllowType(string typeName)
    {
        this.Options.AllowedTypes.Add(typeName);
        return this;
    }
}
