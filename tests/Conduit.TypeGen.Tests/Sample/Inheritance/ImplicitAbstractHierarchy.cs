using Conduit.Attributes;

namespace Conduit.TypeGen.Tests.Sample.Inheritance;

// Abstract base is NOT [ClientVisible]. It's reached only via RoomLayout.Fixtures.
public abstract record FixtureBase
{
    public string Id { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

public sealed record DoorFixture : FixtureBase
{
    public bool IsOpen { get; init; }
}

public sealed record WindowFixture : FixtureBase
{
    public double Transparency { get; init; }
}

[ClientVisible]
public sealed record RoomLayout
{
    public string RoomName { get; init; } = "";
    public IList<FixtureBase> Fixtures { get; init; } = [];
}
