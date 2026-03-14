namespace Conduit.Attributes;

/// <summary>
/// Marks a message type for TypeScript client code generation.
/// Types with this attribute will have TypeScript interfaces emitted
/// by the Conduit.TypeGen build task. This attribute has no runtime
/// authorization effect — use <c>AddConduitTypeFilter</c> for access control.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ClientVisibleAttribute : Attribute;
