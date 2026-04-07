using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        // Collect all type declarations
        var typeDeclarations = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, cancellationToken) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, cancellationToken) as INamedTypeSymbol)
            .Where(static symbol => symbol != null)
            .Select(static (symbol, _) => symbol!);

        // Combine with compilation
        var compilationAndTypes = context.CompilationProvider
            .Combine(typeDeclarations.Collect());

        context.RegisterSourceOutput(compilationAndTypes, static (spc, source) =>
        {
            var (compilation, typeSymbols) = source;
            Execute(compilation, typeSymbols, spc);
        });
    }

    private static void Execute(
        Compilation compilation,
        ImmutableArray<INamedTypeSymbol> typeSymbols,
        SourceProductionContext context)
    {
        var dispatchableTypes = RoslynScanner.Scan(compilation);

        if (dispatchableTypes.Count == 0)
        {
            return;
        }

        // Find handlers and listeners
        var handlers = new List<HandlerInfo>();
        var listenerGroups = new Dictionary<string, List<string>>();

        foreach (var typeSymbol in typeSymbols)
        {
            if (typeSymbol.IsAbstract || typeSymbol.TypeKind != TypeKind.Class)
            {
                continue;
            }

            foreach (var iface in typeSymbol.AllInterfaces)
            {
                var ifaceOriginal = iface.OriginalDefinition.ToDisplayString();

                if (ifaceOriginal == "Conduit.Handlers.ICommandHandler<TCommand, TResult>" && iface.TypeArguments.Length == 2)
                {
                    var commandFullName = iface.TypeArguments[0].ToDisplayString();
                    handlers.Add(new HandlerInfo
                    {
                        MessageFullTypeName = commandFullName,
                        HandlerFullTypeName = typeSymbol.ToDisplayString(),
                    });
                }
                else if (ifaceOriginal == "Conduit.Handlers.ICommandHandler<TCommand>" && iface.TypeArguments.Length == 1)
                {
                    var commandFullName = iface.TypeArguments[0].ToDisplayString();
                    handlers.Add(new HandlerInfo
                    {
                        MessageFullTypeName = commandFullName,
                        HandlerFullTypeName = typeSymbol.ToDisplayString(),
                    });
                }
                else if (ifaceOriginal == "Conduit.Handlers.IQueryHandler<TQuery, TResult>" && iface.TypeArguments.Length == 2)
                {
                    var queryFullName = iface.TypeArguments[0].ToDisplayString();
                    handlers.Add(new HandlerInfo
                    {
                        MessageFullTypeName = queryFullName,
                        HandlerFullTypeName = typeSymbol.ToDisplayString(),
                    });
                }
                else if (ifaceOriginal == "Conduit.Handlers.IEventListener<TEvent>" && iface.TypeArguments.Length == 1)
                {
                    var eventFullName = iface.TypeArguments[0].ToDisplayString();
                    if (!listenerGroups.TryGetValue(eventFullName, out var listeners))
                    {
                        listeners = new List<string>();
                        listenerGroups[eventFullName] = listeners;
                    }

                    listeners.Add(typeSymbol.ToDisplayString());
                }
            }
        }

        // Emit diagnostics
        var remotePatterns = GetRemoteContractPatterns(compilation);
        EmitDiagnostics(context, dispatchableTypes, handlers, remotePatterns);

        // Generate dispatch methods
        var dispatchSource = DispatchEmitter.Emit(dispatchableTypes, handlers);
        context.AddSource("ConduitDispatch.g.cs", dispatchSource);

        // Generate mediator (build groups first — shared with registration)
        var groups = listenerGroups.Select(kvp => new ListenerGroup
        {
            EventFullTypeName = kvp.Key,
            ListenerFullTypeNames = kvp.Value.AsReadOnly(),
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
