namespace Conduit.Analysis;

/// <summary>
/// Describes a discovered command, query, or event type for use by source generators
/// and tooling emitters.
/// </summary>
public sealed class DispatchableTypeInfo
{
    /// <summary>The short type name (e.g. "CreateItemCommand").</summary>
    public required string TypeName { get; init; }

    /// <summary>The fully-qualified type name including namespace.</summary>
    public required string FullTypeName { get; init; }

    /// <summary>The containing namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>The kind of dispatchable (command, query, or event).</summary>
    public required DispatchableKind Kind { get; init; }

    /// <summary>The short name of the result type, or <see langword="null"/> for void commands and events.</summary>
    public string? ResultTypeName { get; init; }

    /// <summary>The fully-qualified result type name, or <see langword="null"/> for void commands and events.</summary>
    public string? ResultFullTypeName { get; init; }

    /// <summary>Whether the type has a <c>[LocalOnly]</c> attribute (events only).</summary>
    public bool IsLocalOnly { get; init; }

    /// <summary>Whether the type has a <c>[FireAndForget]</c> attribute.</summary>
    public bool IsFireAndForget { get; init; }
}
