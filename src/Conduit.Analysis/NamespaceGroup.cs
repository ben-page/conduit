using System.Collections.Generic;

namespace Conduit.Analysis;

public sealed class NamespaceGroup
{
    public required string Namespace { get; init; }
    public required string FileName { get; init; }
    public required IReadOnlyList<DispatchableTypeInfo> Types { get; init; }
}
