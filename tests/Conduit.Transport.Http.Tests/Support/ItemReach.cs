using Conduit.Attributes;
using Conduit.Messages;

namespace Conduit.Transport.Http.Tests.Support;

[ClosedSet("self", "reports", "any")]
public sealed record ItemReach : IClosedSet<ItemReach>
{
    public static readonly ItemReach Self = new("self");
    public static readonly ItemReach Reports = new("reports");
    public static readonly ItemReach Any = new("any");

    private ItemReach(string value) => this.Value = value;

    public static IReadOnlyList<ItemReach> All { get; } = [Self, Reports, Any];

    public string Value { get; }
}
