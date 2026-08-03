namespace Conduit.Attributes;

/// <summary>
/// Suppresses payload capture for this message type in the audit trail.
/// The dispatch is still recorded, but the request and response bodies are omitted.
/// Apply to types carrying PII or large payloads that should not be persisted.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
public sealed class DoNotAuditAttribute : Attribute;
