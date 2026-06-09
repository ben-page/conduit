namespace Conduit.Attributes;

/// <summary>
/// Excludes a handler or event listener from source-generator auto-registration.
/// Use for components wired explicitly via a feature's <c>Add*</c> extension
/// (e.g. <c>LocalAuditListener</c> via <c>AddAuditListener</c>, which also registers its dependencies).
/// The type is still resolvable and dispatchable when registered manually.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NotAutoRegisteredAttribute : Attribute;
