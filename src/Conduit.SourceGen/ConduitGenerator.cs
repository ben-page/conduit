using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;

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

        context.RegisterSourceOutput(model, static (context, model) => Emit(model, context));
    }

    private static ConduitModel BuildModel(Compilation compilation, CancellationToken cancellationToken)
    {
        var dispatchableTypes = RoslynScanner.Scan(compilation);
        var scan = RoslynScanner.ScanHandlers(compilation);

        cancellationToken.ThrowIfCancellationRequested();

        var listenerGroups = scan.ListenerGroups
            .OrderBy(kvp => kvp.Key, System.StringComparer.Ordinal)
            .Select(kvp => new ListenerGroup
            {
                EventFullTypeName = kvp.Key,
                ListenerFullTypeNames = kvp.Value.ToEquatableArray(),
                IsLocalOnly = dispatchableTypes.Any(t => t.FullTypeName == kvp.Key && t.IsLocalOnly),
            }).ToEquatableArray();

        return new ConduitModel
        {
            DispatchableTypes = dispatchableTypes.ToEquatableArray(),
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

        EmitDiagnostics(context, dispatchableTypes, handlers, model.InaccessibleHandlerMessageTypes, model.RemoteContractPatterns);

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

    private static bool IsRemoteContract(string fullTypeName, EquatableArray<string> remotePatterns)
    {
        foreach (var pattern in remotePatterns)
        {
            if (fullTypeName.StartsWith(pattern, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void EmitDiagnostics(
        SourceProductionContext context,
        EquatableArray<DispatchableTypeInfo> types,
        EquatableArray<HandlerInfo> handlers,
        EquatableArray<string> inaccessibleHandlerMessageTypes,
        EquatableArray<string> remotePatterns)
    {
        foreach (var type in types)
        {
            if (type.AuditMarkConflicts is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ConflictingAuditMarks, Location.None, type.FullTypeName, type.AuditMarkConflicts));
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
                    Location.None,
                    type.FullTypeName,
                    GetExpectedHandlerInterface(type)));
            }
            else if (matchingHandlers.Count > 1)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MultipleHandlers,
                    Location.None,
                    type.FullTypeName));
            }

            // [FireAndForget] is only valid on void commands and events
            if (type.IsFireAndForget && type.Kind is DispatchableKind.Command or DispatchableKind.Query)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.InvalidFireAndForget,
                    Location.None,
                    type.FullTypeName));
            }
        }

        // Also check events for invalid [FireAndForget] (events are skipped above)
        // Events ARE valid for [FireAndForget], so no diagnostic needed — this loop only
        // catches commands/queries. Events with [FireAndForget] are fine.

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

    private static string GetExpectedHandlerInterface(DispatchableTypeInfo type)
    {
        return type.Kind switch
        {
            DispatchableKind.Command => $"ICommandHandler<{type.TypeName}, {type.ResultTypeName}>",
            DispatchableKind.VoidCommand => $"ICommandHandler<{type.TypeName}>",
            DispatchableKind.Query => $"IQueryHandler<{type.TypeName}, {type.ResultTypeName}>",
            DispatchableKind.Event => $"IEventListener<{type.TypeName}>",
            DispatchableKind.Model or _ => "IHandler",
        };
    }
}
