using System.Collections.Generic;

namespace Conduit.Analysis;

/// <summary>
/// A group of dispatchable types sharing a common namespace, used to organize
/// generated output files.
/// </summary>
public sealed class NamespaceGroup
{
    /// <summary>The namespace shared by the types in this group.</summary>
    public required string Namespace { get; init; }

    /// <summary>The suggested output file name for this group.</summary>
    public required string FileName { get; init; }

    /// <summary>The dispatchable types in this group.</summary>
    public required IReadOnlyList<DispatchableTypeInfo> Types { get; init; }
}
