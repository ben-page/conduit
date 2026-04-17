using Conduit.Attributes;

// Fixtures live in a separate namespace so they are not filtered out by
// TypeCollector's Conduit.* namespace exclusion (which covers framework types).
// This mirrors GtlHmi's real scenario: domain types in a non-Conduit namespace
// cannot annotate with [ClientVisible] but are reached via a [ClientVisible] root.
namespace SampleDomain.Layouts;

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
