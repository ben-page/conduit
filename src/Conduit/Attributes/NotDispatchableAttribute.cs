namespace Conduit.Attributes;

/// <summary>
/// Excludes a command, query, or event from the generated wire dispatcher,
/// even if it would otherwise be included by namespace filtering.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NotDispatchableAttribute : Attribute;
