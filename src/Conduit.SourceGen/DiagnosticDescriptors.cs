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

    /// <summary>CONDUIT005: A message member is marked both [DoNotAudit] and [AuditRedacted].</summary>
    public static readonly DiagnosticDescriptor ConflictingAuditMarks = new(
        "CONDUIT005", "Conflicting audit marks",
        "'{0}' marks member(s) {1} both [DoNotAudit] and [AuditRedacted]; an audited member takes one",
        "Conduit", DiagnosticSeverity.Error, true);

    /// <summary>CONDUIT006: [AuditAs] is on a member that is not JsonElement, or names no sibling string property.</summary>
    public static readonly DiagnosticDescriptor MisappliedAuditAs = new(
        "CONDUIT006", "Misapplied [AuditAs]",
        "'{0}' misapplies [AuditAs]: {1}; [AuditAs] goes on a JsonElement or JsonElement? member and names a sibling string property holding the message type name",
        "Conduit", DiagnosticSeverity.Error, true);

    /// <summary>CONDUIT007: A message type is generic (or nested in a generic type), so it has no full type name to dispatch by.</summary>
    public static readonly DiagnosticDescriptor GenericMessageType = new(
        "CONDUIT007", "Generic message types are not supported",
        "'{0}' is a generic message type; a message is dispatched by its full type name, so it cannot have type parameters or be nested in a generic type. Declare one non-generic type per message.",
        "Conduit", DiagnosticSeverity.Error, true);

    /// <summary>CONDUIT008: Two or more assemblies declare a message type with the same full type name.</summary>
    public static readonly DiagnosticDescriptor DuplicateMessageType = new(
        "CONDUIT008", "Message type declared in more than one assembly",
        "'{0}' is declared as a message type in more than one assembly ({1}); a message's full type name is its wire name and must name one type. Rename or move one of them.",
        "Conduit", DiagnosticSeverity.Error, true);
}
