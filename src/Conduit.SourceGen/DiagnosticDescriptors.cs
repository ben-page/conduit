using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen;

public static class DiagnosticDescriptors
{
    public static readonly DiagnosticDescriptor NoHandler = new(
        "CONDUIT001", "No handler found",
        "No handler found for {0}, create a class implementing {1}",
        "Conduit", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor MultipleHandlers = new(
        "CONDUIT002", "Multiple handlers found",
        "Multiple handlers found for {0}, only one handler per command/query is allowed",
        "Conduit", DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor InvalidDispatchable = new(
        "CONDUIT003", "Invalid [Dispatchable] usage",
        "{0} has [Dispatchable] but does not implement ICommand, IQuery, or IEvent",
        "Conduit", DiagnosticSeverity.Warning, true);

    public static readonly DiagnosticDescriptor MissingAuthAttribute = new(
        "CONDUIT004", "Missing authorization attribute",
        "{0} is dispatchable but has neither [Authorize] nor [AllowAnonymous]",
        "Conduit", DiagnosticSeverity.Warning, true);
}
