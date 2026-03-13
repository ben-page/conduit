namespace Conduit.Attributes;

/// <summary>
/// Marks a command, query, or event for inclusion in the generated wire dispatcher,
/// even if it would otherwise be excluded by namespace filtering.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class DispatchableAttribute : Attribute;
