namespace Conduit.SourceGen;

/// <summary>
/// A message full type name declared by more than one assembly (CONDUIT008). No copy is emitted: the name is the wire
/// name and the key in every generated switch, so it must name one type.
/// </summary>
public sealed record DuplicateMessageType
{
    /// <summary>The shared metadata full type name.</summary>
    public required string FullTypeName { get; init; }

    /// <summary>The declaring assemblies' names (<c>", "</c>-joined, ordinal-sorted).</summary>
    public required string Assemblies { get; init; }
}
