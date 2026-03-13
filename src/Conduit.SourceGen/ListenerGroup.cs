using System.Collections.Generic;

namespace Conduit.SourceGen;

public sealed class ListenerGroup
{
    public required string EventFullTypeName { get; init; }
    public required IReadOnlyList<string> ListenerFullTypeNames { get; init; }
}
