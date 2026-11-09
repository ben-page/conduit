using Conduit.Analysis;

namespace Conduit.SourceGen;

/// <summary>
/// Describes a discovered handler: which message type it handles and its implementation type.
/// </summary>
public sealed record HandlerInfo
{
    /// <summary>The fully-qualified type name of the command or query this handler processes.</summary>
    public required string MessageFullTypeName { get; init; }

    /// <summary>The fully-qualified type name of the handler implementation class.</summary>
    public required string HandlerFullTypeName { get; init; }

    /// <summary>
    /// Where the handler is declared, when that is in the source being compiled; <see langword="null"/> for a handler from
    /// a referenced assembly.
    /// </summary>
    public SourceLocation? Location { get; init; }
}
