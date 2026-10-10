namespace Conduit.Messages;

/// <summary>
/// A closed set of string-valued members: a type whose constructor is private, so every instance is one of
/// <see cref="All"/>, each spelled on the wire as its <see cref="Value"/>. Unlike an <c>enum</c>, no cast can fabricate a
/// member, and a member's spelling need not be a C# identifier (<c>self</c>, <c>on-hold</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>On the wire a member is its value.</b> <c>ConduitRuntime.JsonOptions</c> writes it as its bare <see cref="Value"/>
/// and reads back only a string some member spells (Ordinal); anything else is refused like an unknown enum name -- a
/// <c>BadRequest</c> naming its JSON path. A JSON <c>null</c> reads as <see langword="null"/>: whether a member may be
/// absent is the declaring contract's to enforce.
/// </para>
/// <para>
/// <b>State the values again in <see cref="Conduit.Attributes.ClosedSetAttribute"/>.</b> Conduit.TypeGen reads compiled
/// metadata and cannot run the static initializers that create the members, so the attribute is the only place it can
/// read them from; it emits the type as a string-literal union of them. The runtime checks the attribute against
/// <see cref="All"/> (same values, same order) the first time it serializes the type, and throws on any difference.
/// </para>
/// </remarks>
/// <typeparam name="TSelf">The closed-set type itself.</typeparam>
public interface IClosedSet<TSelf>
    where TSelf : class, IClosedSet<TSelf>
{
    /// <summary>Gets every member, in declaration order -- the order the generated union lists them in.</summary>
    public static abstract IReadOnlyList<TSelf> All { get; }

    /// <summary>Gets the member's wire spelling.</summary>
    public string Value { get; }
}
