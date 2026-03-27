using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen;

/// <summary>
/// Diagnostic descriptors for Conduit source generator warnings and errors.
/// </summary>
public static class DiagnosticDescriptors
{
    /// <summary>CONDUIT001: A command or query has no local handler and is not marked as a remote contract.</summary>
    public static readonly DiagnosticDescriptor NoHandler = new(
        "CONDUIT001", "No handler found",
        "No handler found for {0}, create a class implementing {1}",
        "Conduit", DiagnosticSeverity.Warning, true);

    /// <summary>CONDUIT002: Multiple handlers found for a command or query (only one is allowed).</summary>
    public static readonly DiagnosticDescriptor MultipleHandlers = new(
        "CONDUIT002", "Multiple handlers found",
        "Multiple handlers found for {0}, only one handler per command/query is allowed",
        "Conduit", DiagnosticSeverity.Error, true);
}
