// Types no [ClientVisible] type reaches, so no test that scans this assembly sees them. A test hands them in as roots or
// maps them directly.

// Two DTOs with one short name: emitted together they would be two `export interface Item` declarations, which
// TypeScript merges without a word. TypeShapesShould hands their roots to MergeFileEmitter to see generation refuse.
namespace SampleDomain.Orders
{
    public sealed record Item(string Sku, int Quantity);

    public sealed record OrderLines(IReadOnlyList<Item> Items);
}

namespace SampleDomain.Catalog
{
    public sealed record Item(string Title);

    public sealed record CatalogPage(Item Featured);
}

// A third Item, reached only through an always-ignored property: never on the wire, so never walked, and never a
// collision with Catalog.Item.
namespace SampleDomain.Billing
{
    public sealed record Item(string LedgerCode);

    public sealed record Invoice(string Number)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public Item? Internal { get; init; }
    }
}

// A host type that shares a BCL type's short name is the host's type, not the BCL one.
namespace SampleDomain.Lookalikes
{
    public sealed record Uri(string Value);
}
