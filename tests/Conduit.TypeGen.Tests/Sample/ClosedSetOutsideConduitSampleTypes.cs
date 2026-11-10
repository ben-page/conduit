using Conduit.Attributes;
using Conduit.Messages;

// A closed set in a NON-Conduit namespace, so "a closed set gets no interface" is proven where TypeCollector walks a
// host's domain types (it once skipped every Conduit* namespace, TestReach's among them; it now skips only Conduit's own).
// Reached only through the [ClientVisible] DTO below, the way a host's domain types are.
namespace SampleDomain.ClosedSets;

[ClosedSet("plain", "carriage\rreturn", "line\u2028separator", "paragraph\u2029separator")]
public sealed record OutsideReach : IClosedSet<OutsideReach>
{
    public static readonly OutsideReach Plain = new("plain");
    public static readonly OutsideReach CarriageReturn = new("carriage\rreturn");
    public static readonly OutsideReach LineSeparator = new("line\u2028separator");
    public static readonly OutsideReach ParagraphSeparator = new("paragraph\u2029separator");

    private OutsideReach(string value) => this.Value = value;

    public static IReadOnlyList<OutsideReach> All { get; } = [Plain, CarriageReturn, LineSeparator, ParagraphSeparator];

    public string Value { get; }
}

[ClientVisible]
public sealed record OutsideReachDto(string Key, OutsideReach Reach);
