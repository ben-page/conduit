using System.Collections.Generic;
using System.Linq;
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
        // Handlers and listeners are discovered from the whole compilation (including referenced
        // assemblies) via RoslynScanner, so the source output depends on the compilation alone.
        context.RegisterSourceOutput(context.CompilationProvider, static (spc, compilation) => Execute(compilation, spc));
    }

    private static void Execute(
        Compilation compilation,
        SourceProductionContext context)
    {
        var dispatchableTypes = RoslynScanner.Scan(compilation);

        if (dispatchableTypes.Count == 0)
        {
            return;
        }

        // Find handlers and listeners across the whole compilation (including referenced assemblies)
        var scan = RoslynScanner.ScanHandlers(compilation);
        var handlers = scan.Handlers;

        // Emit diagnostics
        var remotePatterns = GetRemoteContractPatterns(compilation);
        EmitDiagnostics(context, dispatchableTypes, handlers, remotePatterns);

        // Generate dispatch methods
        var dispatchSource = DispatchEmitter.Emit(dispatchableTypes, handlers);
        context.AddSource("ConduitDispatch.g.cs", dispatchSource);

        // Generate mediator (build groups first — shared with registration)
        var groups = scan.ListenerGroups.Select(kvp => new ListenerGroup
        {
            EventFullTypeName = kvp.Key,
            ListenerFullTypeNames = kvp.Value,
            IsLocalOnly = dispatchableTypes.Any(t => t.FullTypeName == kvp.Key && t.IsLocalOnly),
        }).ToList();

        // Generate DI registration (interface-based)
        var registrationSource = RegistrationEmitter.Emit(handlers, groups, dispatchableTypes);
        context.AddSource("ConduitRegistration.g.cs", registrationSource);

        var mediatorSource = MediatorEmitter.Emit(dispatchableTypes, handlers, groups);
        context.AddSource("ConduitMediator.g.cs", mediatorSource);

        // Generate REST endpoint mappings (only if Conduit.Transport.Http is referenced)
        var restHandlersType = compilation.GetTypeByMetadataName(EmitConstants.ConduitRestHandlers);
        if (restHandlersType != null)
        {
            var restSource = RestEmitter.Emit(dispatchableTypes);
            context.AddSource("ConduitRestEndpoints.g.cs", restSource);
        }

        // Generate resilience dispatch (only if Conduit.Resilience is referenced)
        var resilienceType = compilation.GetTypeByMetadataName(EmitConstants.ResilienceBehavior);
        if (resilienceType != null)
        {
            var resilienceSource = ResilienceEmitter.Emit(dispatchableTypes);
            context.AddSource("ConduitResilienceDispatch.g.cs", resilienceSource);
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

    private static bool IsRemoteContract(string fullTypeName, List<string> remotePatterns)
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
        IReadOnlyList<DispatchableTypeInfo> types,
        IReadOnlyList<HandlerInfo> handlers,
        List<string> remotePatterns)
    {
        foreach (var type in types)
        {
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
