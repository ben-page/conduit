using System.Collections.Generic;

namespace Conduit.SourceGen;

/// <summary>
/// Groups all discovered event listeners for a single event type.
/// </summary>
public sealed class ListenerGroup
{
    /// <summary>The fully-qualified type name of the event.</summary>
    public required string EventFullTypeName { get; init; }

    /// <summary>The fully-qualified type names of all listener implementations for this event.</summary>
    public required IReadOnlyList<string> ListenerFullTypeNames { get; init; }

    /// <summary>Whether the event is marked <c>[LocalOnly]</c>, suppressing transport broadcast.</summary>
    public bool IsLocalOnly { get; init; }
}
