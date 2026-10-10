namespace Conduit.Attributes;

/// <summary>
/// States a closed set's wire values (<see cref="Conduit.Messages.IClosedSet{TSelf}"/>) for Conduit.TypeGen, which reads
/// compiled metadata and so cannot run the static initializers that create the members. List exactly the type's
/// <c>All</c> values, in <c>All</c>'s order: the runtime checks the two agree the first time it serializes the type, and
/// TypeGen refuses this attribute on a type that does not implement <c>IClosedSet&lt;itself&gt;</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ClosedSetAttribute : Attribute
{
    /// <summary>Initializes a new instance of the <see cref="ClosedSetAttribute"/> class.</summary>
    /// <param name="values">Every member's wire value, in <c>All</c>'s order.</param>
    public ClosedSetAttribute(params string[] values) => this.Values = values;

    /// <summary>Gets every member's wire value, in <c>All</c>'s order.</summary>
    public IReadOnlyList<string> Values { get; }
}
