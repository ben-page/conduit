namespace Conduit.Analysis;

/// <summary>
/// Classifies a dispatchable type for code generation.
/// </summary>
public enum DispatchableKind
{
    /// <summary>A command that returns a result.</summary>
    Command,

    /// <summary>A command with no return value.</summary>
    VoidCommand,

    /// <summary>A query that returns a result.</summary>
    Query,

    /// <summary>An event published to multiple listeners.</summary>
    Event
}
