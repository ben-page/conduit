using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Conduit.Runtime;

/// <summary>
/// The shape a <see cref="MessageContext"/> correlation id must have to be taken from a caller. An inbound edge that
/// trusts its callers' ids (the HTTP wire endpoint mapped with <c>trustCallerContext: true</c>, the SignalR hub with
/// <c>TrustCallerContext</c>) honors them only when every supplied id passes <see cref="IsValid"/>, and mints fresh
/// ones otherwise: an id lands in log lines, audit rows, the inbox's dedup key and the headers a transport forwards.
/// </summary>
public static partial class MessageContextIds
{
    /// <summary>
    /// Whether <paramref name="id"/> is a plain token: 1 to 64 ASCII letters, digits and hyphens
    /// (<c>^[A-Za-z0-9-]{1,64}$</c>). Both <see cref="Guid"/> formats, <c>"N"</c> (what Conduit mints) and <c>"D"</c>, pass;
    /// null, empty, whitespace, CR/LF and any non-ASCII character fail.
    /// </summary>
    /// <param name="id">The candidate id, as a caller supplied it.</param>
    /// <returns><see langword="true"/> when the id may be honored.</returns>
    public static bool IsValid([NotNullWhen(true)] string? id)
    {
        if (id is null)
        {
            return false;
        }

        // '$' also matches before a final '\n' ("abc\n" matches "abc"), so the match must span the whole id.
        var match = PlainToken().Match(id);
        return match.Success && match.Length == id.Length;
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex PlainToken();
}
