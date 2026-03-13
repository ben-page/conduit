namespace Conduit.Attributes;

/// <summary>
/// Registers a namespace for automatic wire-dispatch inclusion. All commands, queries,
/// and events in the specified namespace (and its children) will be included in the
/// generated wire dispatcher unless explicitly excluded with <see cref="NotDispatchableAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class DispatchableNamespaceAttribute(string @namespace) : Attribute
{
    /// <summary>
    /// The namespace to include in wire dispatch generation.
    /// </summary>
    public string Namespace => @namespace;
}
