namespace Conduit.Attributes;

/// <summary>
/// Keeps a value out of the audit trail. On a message TYPE: the dispatch is still recorded, but no request body is
/// captured. On a PROPERTY (or a positional record's parameter): the body is captured without that member. Apply to
/// credentials, pay and other values that must never be persisted in an audit record.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class DoNotAuditAttribute : Attribute;
