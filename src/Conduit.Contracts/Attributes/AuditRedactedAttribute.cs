namespace Conduit.Attributes;

/// <summary>
/// Records that a value is present, and that it changed, but never the value or anything derived from it (no hash,
/// no length, no last four). On a PROPERTY or a positional record's PARAMETER: the audit payload carries
/// <see cref="Placeholder"/> when the member has a value, and null or <c>""</c> as itself. Use it where *that* a
/// value changed matters and the value must never be persisted; use <see cref="DoNotAuditAttribute"/> where even
/// presence is not wanted. The two cannot be combined on one member (CONDUIT005; refused at serialization too).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class AuditRedactedAttribute : Attribute
{
    /// <summary>What an audit record holds in place of a present redacted value.</summary>
    public const string Placeholder = "•••";
}
