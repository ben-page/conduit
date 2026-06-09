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

    /// <summary>CONDUIT003: [FireAndForget] is only valid on void commands (ICommand) and events (IEvent).</summary>
    public static readonly DiagnosticDescriptor InvalidFireAndForget = new(
        "CONDUIT003", "Invalid [FireAndForget] usage",
        "[FireAndForget] on {0} is not supported — only void commands (ICommand) and events (IEvent) can be fire-and-forget. Commands with results and queries require a response.",
        "Conduit", DiagnosticSeverity.Warning, true);

    /// <summary>CONDUIT004: A handler exists in a referenced assembly but is not accessible to the generating assembly.</summary>
    public static readonly DiagnosticDescriptor HandlerNotAccessible = new(
        "CONDUIT004", "Handler not accessible",
        "Handler for {0} exists but is not accessible to the generating assembly. Make it public to be auto-registered.",
        "Conduit", DiagnosticSeverity.Warning, true);
}
