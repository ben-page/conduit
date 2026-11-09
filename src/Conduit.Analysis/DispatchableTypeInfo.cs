namespace Conduit.Analysis;

/// <summary>
/// Describes a discovered command, query, or event type for use by source generators
/// and tooling emitters.
/// </summary>
public sealed record DispatchableTypeInfo
{
    /// <summary>The short type name (e.g. "CreateItemCommand").</summary>
    public required string TypeName { get; init; }

    /// <summary>The fully-qualified type name including namespace.</summary>
    public required string FullTypeName { get; init; }

    /// <summary>The containing namespace.</summary>
    public required string Namespace { get; init; }

    /// <summary>The kind of dispatchable (command, query, or event).</summary>
    public required DispatchableKind Kind { get; init; }

    /// <summary>The short name of the result type, or <see langword="null"/> for void commands and events.</summary>
    public string? ResultTypeName { get; init; }

    /// <summary>The fully-qualified result type name, or <see langword="null"/> for void commands and events.</summary>
    public string? ResultFullTypeName { get; init; }

    /// <summary>
    /// <see langword="true"/> when a reference-type result is declared <c>T?</c>; TypeGen only, so SourceGen's scanner
    /// leaves it <see langword="false"/>.
    /// </summary>
    public bool ResultIsNullable { get; init; }

    /// <summary>
    /// Full type name of the TypeScript base interface this type extends, or <see langword="null"/>
    /// if the type has no inheritance relationship in the emitted output. Populated by
    /// <c>Conduit.TypeGen.ReflectionScanner</c> when discovering concrete derived types of
    /// an abstract <c>[ClientVisible]</c> base.
    /// </summary>
    public string? BaseFullTypeName { get; init; }

    /// <summary>
    /// Short (unqualified) name of the TypeScript base interface, used by emitters to produce
    /// <c>extends BaseName</c> clauses. Null when <see cref="BaseFullTypeName"/> is null.
    /// </summary>
    public string? BaseTypeName { get; init; }

    /// <summary>Whether the type has a <c>[LocalOnly]</c> attribute (events only).</summary>
    public bool IsLocalOnly { get; init; }

    /// <summary>
    /// Whether the type is marked <c>[ClientVisible]</c>, on itself or a base (the attribute is inherited). SourceGen's
    /// scanner sets it, for the registry's <c>ClientVisibleEventTypeNames</c>; TypeGen reads the attribute itself
    /// (<c>ReflectionScanner.ScanClientVisible</c>), so its scanner leaves it <see langword="false"/>.
    /// </summary>
    public bool IsClientVisible { get; init; }

    /// <summary>Whether the type has a <c>[FireAndForget]</c> attribute.</summary>
    public bool IsFireAndForget { get; init; }

    /// <summary>Whether the <c>[FireAndForget]</c> attribute sets <c>Immediate = true</c>
    /// (bypass transactional outbox capture).</summary>
    public bool IsFireAndForgetImmediate { get; init; }

    /// <summary>Whether the type has a <c>[DoNotAudit]</c> attribute.</summary>
    public bool IsDoNotAudit { get; init; }

    /// <summary>Members marked both <c>[DoNotAudit]</c> and <c>[AuditRedacted]</c> (comma-joined, ordinal-sorted), or
    /// <see langword="null"/>. A string, not an array, so the record's value equality holds (CONDUIT005).</summary>
    public string? AuditMarkConflicts { get; init; }

    /// <summary>Members whose <c>[AuditAs]</c> is misapplied, each with its reason (<c>"; "</c>-joined, ordinal-sorted), or
    /// <see langword="null"/>. A string, not an array, so the record's value equality holds (CONDUIT006).</summary>
    public string? AuditAsMisuses { get; init; }
}
