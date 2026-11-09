namespace Conduit.Analysis;

/// <summary>
/// Where a type is declared in the source being compiled: the syntax tree's file path and the span of the declaration's
/// name. Plain strings and ints, so a model holding it stays value-equatable (no Roslyn <c>Location</c> or syntax tree
/// is captured); the source generator turns it back into a location in the compilation's own tree when it reports a
/// diagnostic.
/// </summary>
/// <param name="FilePath">The declaring syntax tree's <c>FilePath</c>.</param>
/// <param name="Start">The start of the declaration name's span in that tree.</param>
/// <param name="Length">The length of that span.</param>
public sealed record SourceLocation(string FilePath, int Start, int Length);
