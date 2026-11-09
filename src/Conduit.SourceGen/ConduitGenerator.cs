using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Conduit.SourceGen;

/// <summary>
/// Roslyn incremental source generator that discovers dispatchable types and handlers,
/// then emits the mediator, dispatch methods, DI registration, and REST endpoint mappings.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class ConduitGenerator : IIncrementalGenerator
{
    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // The discovery walk needs the whole compilation (cross-assembly, by-interface), so the model
        // derives from CompilationProvider. BuildModel produces a value-equatable ConduitModel, letting
        // Roslyn cache the emission when discovery is unchanged. (The walk itself still re-runs each
        // change because the Compilation always changes — see Conduit.SourceGen/CLAUDE.md.)
        var model = context.CompilationProvider
            .Select(static (compilation, cancellationToken) => BuildModel(compilation, cancellationToken))
            .WithTrackingName("BuildModel");

        // The emission reads the model without source locations: they move with any edit above a declaration (a comment
        // at the top of a file, a property added to an earlier record), and only the diagnostics need them.
        var emission = model
            .Select(static (model, _) => model.WithoutLocations())
            .WithTrackingName("EmissionModel");

        context.RegisterSourceOutput(emission, static (context, model) => Emit(model, context));

        // A diagnostic is located in the compilation's own syntax tree, which only the compilation holds: a location in
        // a tree is what a #pragma and the IDE apply to (a file path and span alone print as line 1 and suppress
        // nothing). So the diagnostics are a second output, over the full model and the compilation; it re-runs on
        // every change, as the walk does, while the emission above stays cached.
        context.RegisterSourceOutput(
            model.Combine(context.CompilationProvider),
            static (context, input) => ReportDiagnostics(input.Left, input.Right, context));
    }

    private static ConduitModel BuildModel(Compilation compilation, CancellationToken cancellationToken)
    {
        var messageSymbols = new List<INamedTypeSymbol>();
        var genericMessageTypes = new List<string>();
        var duplicateMessageTypes = new List<DuplicateMessageType>();
        var dispatchableTypes = RoslynScanner.Scan(compilation, messageSymbols, genericMessageTypes, duplicateMessageTypes);
        var carriedAuditAsMisuses = RoslynScanner.ScanCarriedAuditAsMisuses(compilation, messageSymbols);
        var scan = RoslynScanner.ScanHandlers(compilation);

        cancellationToken.ThrowIfCancellationRequested();

        // A listener group is registered by its event's name, which a duplicated name cannot be written as.
        var duplicatedNames = new HashSet<string>(duplicateMessageTypes.Select(duplicate => duplicate.FullTypeName), System.StringComparer.Ordinal);
        var listenerGroups = scan.ListenerGroups
            .Where(kvp => !duplicatedNames.Contains(kvp.Key))
            .OrderBy(kvp => kvp.Key, System.StringComparer.Ordinal)
            .Select(kvp => new ListenerGroup
            {
                EventFullTypeName = kvp.Key,
                ListenerFullTypeNames = kvp.Value.ToEquatableArray(),
            }).ToEquatableArray();

        return new ConduitModel
        {
            DispatchableTypes = dispatchableTypes.ToEquatableArray(),
            GenericMessageTypes = genericMessageTypes.ToEquatableArray(),
            DuplicateMessageTypes = duplicateMessageTypes.ToEquatableArray(),
            CarriedAuditAsMisuses = carriedAuditAsMisuses.ToEquatableArray(),
            Handlers = scan.Handlers.ToEquatableArray(),
            ListenerGroups = listenerGroups,
            InaccessibleHandlerMessageTypes = scan.InaccessibleHandlerMessageTypes.ToEquatableArray(),
            RemoteContractPatterns = GetRemoteContractPatterns(compilation).ToEquatableArray(),
            HasHttpTransport = compilation.GetTypeByMetadataName(EmitConstants.ConduitRestHandlers) is not null,
            HasResilience = compilation.GetTypeByMetadataName(EmitConstants.ResilienceBehavior) is not null,
        };
    }

    private static void Emit(ConduitModel model, SourceProductionContext context)
    {
        if (model.DispatchableTypes.Count == 0)
        {
            return;
        }

        var dispatchableTypes = model.DispatchableTypes;
        var handlers = model.Handlers;
        var groups = model.ListenerGroups;

        context.AddSource("ConduitDispatch.g.cs", DispatchEmitter.Emit(dispatchableTypes, handlers));
        context.AddSource("ConduitRegistration.g.cs", RegistrationEmitter.Emit(handlers, groups, dispatchableTypes));
        context.AddSource("ConduitMediator.g.cs", MediatorEmitter.Emit(dispatchableTypes, handlers));

        if (model.HasHttpTransport)
        {
            context.AddSource("ConduitRestEndpoints.g.cs", RestEmitter.Emit(dispatchableTypes));
        }

        if (model.HasResilience)
        {
            context.AddSource("ConduitResilienceDispatch.g.cs", ResilienceEmitter.Emit(dispatchableTypes));
        }
    }

    private static void ReportDiagnostics(ConduitModel model, Compilation compilation, SourceProductionContext context)
    {
        // Reported even when nothing is emitted: a project whose only messages are excluded still learns why.
        foreach (var generic in model.GenericMessageTypes)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.GenericMessageType, Location.None, generic));
        }

        foreach (var duplicate in model.DuplicateMessageTypes)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DuplicateMessageType, Location.None, duplicate.FullTypeName, duplicate.Assemblies));
        }

        if (model.DispatchableTypes.Count == 0)
        {
            return;
        }

        EmitDiagnostics(context, compilation, model.DispatchableTypes, model.Handlers, model.InaccessibleHandlerMessageTypes, model.RemoteContractPatterns);

        foreach (var carried in model.CarriedAuditAsMisuses)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MisappliedAuditAs, Location.None, carried.FullTypeName, carried.Misuses));
        }
    }

    private static List<string> GetRemoteContractPatterns(Compilation compilation)
    {
        const string attributeName = "Conduit.Attributes.RemoteContractsAttribute";
        var patterns = new List<string>();

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == attributeName
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string pattern)
            {
                patterns.Add(pattern);
            }
        }

        return patterns;
    }

    // A pattern names a namespace, matched on whole segments: the namespace itself and those beneath it
    // ("Inventory" covers Inventory.Reserve and Inventory.Counts.Recount, never InventoryAudit.Record), or one type by
    // its full name.
    private static bool IsRemoteContract(string fullTypeName, EquatableArray<string> remotePatterns)
    {
        foreach (var pattern in remotePatterns)
        {
            if (fullTypeName == pattern
                || (fullTypeName.Length > pattern.Length
                    && fullTypeName[pattern.Length] == '.'
                    && fullTypeName.StartsWith(pattern, System.StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitDiagnostics(
        SourceProductionContext context,
        Compilation compilation,
        EquatableArray<DispatchableTypeInfo> types,
        EquatableArray<HandlerInfo> handlers,
        EquatableArray<string> inaccessibleHandlerMessageTypes,
        EquatableArray<string> remotePatterns)
    {
        foreach (var type in types)
        {
            var location = ToLocation(type.Location, compilation);

            if (type.AuditMarkConflicts is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ConflictingAuditMarks, location, type.FullTypeName, type.AuditMarkConflicts));
            }

            if (type.AuditAsMisuses is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MisappliedAuditAs, location, type.FullTypeName, type.AuditAsMisuses));
            }

            if (type.Kind == DispatchableKind.Event)
            {
                continue;
            }

            var matchingHandlers = handlers.Where(h => h.MessageFullTypeName == type.FullTypeName).ToList();

            if (matchingHandlers.Count == 0 && !IsRemoteContract(type.FullTypeName, remotePatterns))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.NoHandler,
                    location,
                    type.FullTypeName,
                    GetExpectedHandlerInterface(type)));
            }
            else if (matchingHandlers.Count > 1)
            {
                // At the message, with each handler as an additional location; at the first handler when the message is
                // declared in a referenced assembly.
                var handlerLocations = matchingHandlers
                    .Select(handler => handler.Location)
                    .Where(handlerLocation => handlerLocation is not null)
                    .OrderBy(handlerLocation => handlerLocation!.FilePath, System.StringComparer.Ordinal)
                    .ThenBy(handlerLocation => handlerLocation!.Start)
                    .Select(handlerLocation => ToLocation(handlerLocation, compilation))
                    .ToList();
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MultipleHandlers,
                    type.Location is null ? handlerLocations.FirstOrDefault() ?? Location.None : location,
                    handlerLocations,
                    type.FullTypeName));
            }

            // [FireAndForget] is only valid on void commands and events
            if (type.IsFireAndForget && type.Kind is DispatchableKind.Command or DispatchableKind.Query)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.InvalidFireAndForget,
                    location,
                    type.FullTypeName));
            }
        }

        // Report message types whose only handler lives in a referenced assembly but is not
        // accessible to the generating assembly (e.g. internal). A type that also has an
        // accessible handler is not reported.
        foreach (var messageType in inaccessibleHandlerMessageTypes)
        {
            if (handlers.Any(h => h.MessageFullTypeName == messageType))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.HandlerNotAccessible,
                Location.None,
                messageType));
        }
    }

    /// <summary>
    /// The location a <see cref="Conduit.Analysis.SourceLocation"/> names, in the compilation's own syntax tree, so a
    /// <c>#pragma</c> and the IDE apply to it; <see cref="Location.None"/> for a type from a referenced assembly.
    /// </summary>
    private static Location ToLocation(SourceLocation? location, Compilation compilation)
    {
        if (location is null)
        {
            return Location.None;
        }

        var span = new TextSpan(location.Start, location.Length);
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree.FilePath == location.FilePath && span.End <= tree.Length)
            {
                return Location.Create(tree, span);
            }
        }

        // The model and the compilation come from one run, so the tree is there; failing that, the file and the span.
        return Location.Create(location.FilePath, span, default);
    }

    // Only a command or a query reaches CONDUIT001: an event has listeners, not a handler.
    private static string GetExpectedHandlerInterface(DispatchableTypeInfo type)
    {
        if (type.Kind == DispatchableKind.Query)
        {
            return $"IQueryHandler<{type.TypeName}, {type.ResultTypeName}>";
        }

        return type.Kind == DispatchableKind.VoidCommand
            ? $"ICommandHandler<{type.TypeName}>"
            : $"ICommandHandler<{type.TypeName}, {type.ResultTypeName}>";
    }
}
