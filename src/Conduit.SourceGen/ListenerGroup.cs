namespace Conduit.SourceGen;

/// <summary>
/// Groups all discovered event listeners for a single event type.
/// </summary>
public sealed record ListenerGroup
{
    /// <summary>The fully-qualified type name of the event.</summary>
    public required string EventFullTypeName { get; init; }

    /// <summary>The fully-qualified type names of all listener implementations for this event.</summary>
    public required EquatableArray<string> ListenerFullTypeNames { get; init; }
}
