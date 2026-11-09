using Conduit.Attributes;

// Outside Conduit.*, so TypeCollector walks it (see ImplicitAbstractHierarchy.cs): the abstract base is reached only
// through LedgerBook.Entries, and its derived types are enumerated from there.
namespace SampleDomain.Accounting;

public abstract record LedgerEntryBase
{
    public string Id { get; init; } = "";
}

public sealed record PostedEntry : LedgerEntryBase
{
    public decimal Amount { get; init; }
}

// [LocalOnly] is inherited: neither this type nor the one deriving from it, which carries no attribute of its own, is
// emitted.
[LocalOnly]
public record ProvisionalEntry : LedgerEntryBase
{
    public string DraftNote { get; init; } = "";
}

public sealed record ReversalEntry : ProvisionalEntry
{
    public string ReversalSecret { get; init; } = "";
}

[ClientVisible]
public sealed record LedgerBook
{
    public IList<LedgerEntryBase> Entries { get; init; } = [];
}
