namespace Conduit.SourceGen;

/// <summary>
/// A type a message carries (not itself a message) whose <c>[AuditAs]</c> is misapplied (CONDUIT006).
/// </summary>
public sealed record CarriedAuditAsMisuse
{
    /// <summary>The carried type's full name.</summary>
    public required string FullTypeName { get; init; }

    /// <summary>Its misapplied members, each with its reason (<c>"; "</c>-joined, ordinal-sorted).</summary>
    public required string Misuses { get; init; }
}
