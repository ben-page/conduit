namespace Conduit.SourceGen;

public sealed class BehaviorInfo
{
    public required string OpenTypeName { get; init; }
    public required int Order { get; init; }
    public required bool IsEventBehavior { get; init; }
}
