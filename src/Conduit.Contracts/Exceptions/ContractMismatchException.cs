using Conduit.Runtime;

namespace Conduit.Exceptions;

/// <summary>
/// Thrown when an inbound wire request is missing a constructor parameter that has no default, at any depth of
/// positional models -- the sender was built against an older contract (an out-of-date browser tab). Maps to HTTP 400 via
/// <see cref="ErrorCode.ContractMismatch"/>. Silently defaulting the member would let a stale client clear a field
/// it does not know about, so the request is refused instead. Carries one <see cref="DispatchErrorDetail"/> per NAMED
/// missing member (<c>Field</c> = the wire path, e.g. <c>eTag</c> or <c>columns[0].wipLimit</c>; <c>Message</c> =
/// <c>"missing"</c>). The runtime names at most 20 and counts the rest, so a large payload cannot make a large error.
/// </summary>
public sealed class ContractMismatchException : ConduitException
{
    /// <summary>Every missing member named.</summary>
    public ContractMismatchException(IReadOnlyList<string> missing)
        : this(missing, (missing ?? throw new ArgumentNullException(nameof(missing))).Count)
    {
    }

    /// <summary><paramref name="named"/> of <paramref name="total"/> missing members named; the rest counted.</summary>
    public ContractMismatchException(IReadOnlyList<string> named, int total)
        : base(ErrorCode.ContractMismatch, MessageFor(named, total), DetailsFor(named))
    {
        this.Text = TextFor(named, total);

        // No public data property: a declared property would make this a client-typed refusal (TypeGen emits it into
        // ErrorMap and the wire error gains Type/Data). The count lives in the message.
    }

    private static string MessageFor(IReadOnlyList<string> named, int total)
    {
        ArgumentNullException.ThrowIfNull(named);
        var more = total > named.Count ? $" and {total - named.Count} more" : string.Empty;
        return $"This page is out of date: the request is missing {string.Join(", ", named)}{more}. Reload to continue.";
    }

    private static RefusalText TextFor(IReadOnlyList<string> named, int total)
    {
        var members = string.Join(", ", named);
        return total > named.Count
            ? new RefusalText(
                ConduitTextNames.ContractMismatchMore,
                new Dictionary<string, object?> { ["members"] = members, ["more"] = total - named.Count })
            : new RefusalText(
                ConduitTextNames.ContractMismatch,
                new Dictionary<string, object?> { ["members"] = members });
    }

    private static DispatchErrorDetail[] DetailsFor(IReadOnlyList<string> named)
    {
        ArgumentNullException.ThrowIfNull(named);
        return [.. named.Select(m => new DispatchErrorDetail(m, "missing"))];
    }
}
