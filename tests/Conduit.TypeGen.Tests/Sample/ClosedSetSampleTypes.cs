using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.TypeGen.Tests.Sample;

// --- Closed sets: emitted as a string-literal union of the DECLARED VALUES (not the member names), parenthesized in an
// array, `| null` when nullable, as a direct and as a list query response. "o'clock" needs escaping in a single-quoted
// TypeScript literal. Reached only through the [ClientVisible] types below -- a closed set needs no [ClientVisible]. ---

[ClosedSet("self", "reports", "o'clock")]
public sealed record TestReach : IClosedSet<TestReach>
{
    public static readonly TestReach Self = new("self");
    public static readonly TestReach Reports = new("reports");
    public static readonly TestReach OClock = new("o'clock");

    private TestReach(string value) => this.Value = value;

    public static IReadOnlyList<TestReach> All { get; } = [Self, Reports, OClock];

    public string Value { get; }
}

[ClientVisible]
public sealed record TestGrantDto(string Key, TestReach Reach, TestReach? Widest, IReadOnlyList<TestReach> Allowed);

[ClientVisible]
public sealed record TestListReachesQuery(int Page) : IQuery<IReadOnlyList<TestReach>>;

[ClientVisible]
public sealed record TestWidestReachQuery(string Key) : IQuery<TestReach>;
