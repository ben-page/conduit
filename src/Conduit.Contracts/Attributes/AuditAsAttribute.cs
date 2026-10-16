namespace Conduit.Attributes;

/// <summary>
/// Types a raw-JSON member at audit capture. On a <c>JsonElement</c> (or <c>JsonElement?</c>) PROPERTY or a positional
/// record's PARAMETER: the member holds a serialized Conduit message whose wire type name (<see cref="System.Type.FullName"/>)
/// is in the sibling string property named <paramref name="typeMember"/>. The audit payload carries that message
/// read into its type and redacted by its own marks (<see cref="DoNotAuditAttribute"/>, <see cref="AuditRedactedAttribute"/>,
/// a nested <see cref="AuditAsAttribute"/>) -- never the raw JSON. The name resolves only among the message types a
/// <c>ConduitTypeRegistry</c> registered (commands, queries, events). A null element stays null; a message type whose
/// dispatch records no body -- marked <see cref="DoNotAuditAttribute"/> at type level, or suppressed by the host's audit
/// registration (Conduit.Audit's <c>Suppress&lt;T&gt;()</c>/<c>SuppressPayload&lt;T&gt;()</c>) -- leaves the member out; an unknown or null name, or JSON that does
/// not read as that type, writes <see cref="AuditRedactedAttribute.Placeholder"/>. Wire serialization is unaffected.
/// A member that is not <c>JsonElement</c>, or names no sibling string property, is a build error (CONDUIT006) on a message
/// and on any type a message carries, in the compilation or in a referenced assembly that references Conduit.Contracts.
/// </summary>
/// <param name="typeMember">The sibling string property holding the inner message's wire type name; use <c>nameof</c>.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class AuditAsAttribute(string typeMember) : Attribute
{
    /// <summary>The sibling string property holding the inner message's wire type name.</summary>
    public string TypeMember => typeMember;
}
