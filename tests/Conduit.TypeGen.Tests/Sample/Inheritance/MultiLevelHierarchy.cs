using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

[ClientVisible]
public abstract record LevelA
{
    public string RootField { get; init; } = string.Empty;
}

public abstract record LevelB : LevelA
{
    public string MidField { get; init; } = string.Empty;
}

public sealed record LevelC : LevelB
{
    public string LeafC { get; init; } = string.Empty;
}

public sealed record LevelD : LevelB
{
    public int LeafD { get; init; }
}
