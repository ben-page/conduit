using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Conduit.SourceGen;

[Generator(LanguageNames.CSharp)]
public sealed class ConduitGenerator : IIncrementalGenerator
{
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
        var handlerTypeNames = new List<string>();
        var listenerTypeNames = new List<string>();

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
                    handlerTypeNames.Add(typeSymbol.ToDisplayString());
                }
                else if (ifaceOriginal == "Conduit.Handlers.ICommandHandler<TCommand>" && iface.TypeArguments.Length == 1)
                {
                    var commandFullName = iface.TypeArguments[0].ToDisplayString();
                    handlers.Add(new HandlerInfo
                    {
                        MessageFullTypeName = commandFullName,
                        HandlerFullTypeName = typeSymbol.ToDisplayString(),
                    });
                    handlerTypeNames.Add(typeSymbol.ToDisplayString());
                }
                else if (ifaceOriginal == "Conduit.Handlers.IQueryHandler<TQuery, TResult>" && iface.TypeArguments.Length == 2)
                {
                    var queryFullName = iface.TypeArguments[0].ToDisplayString();
                    handlers.Add(new HandlerInfo
                    {
                        MessageFullTypeName = queryFullName,
                        HandlerFullTypeName = typeSymbol.ToDisplayString(),
                    });
                    handlerTypeNames.Add(typeSymbol.ToDisplayString());
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
                    listenerTypeNames.Add(typeSymbol.ToDisplayString());
                }
            }
        }

        // Emit diagnostics
        EmitDiagnostics(context, dispatchableTypes, handlers);

        // Generate dispatch methods
        var dispatchSource = DispatchEmitter.Emit(dispatchableTypes, handlers);
        context.AddSource("ConduitDispatch.g.cs", dispatchSource);

        // Generate DI registration
        var registrationSource = RegistrationEmitter.Emit(handlerTypeNames, listenerTypeNames, dispatchableTypes);
        context.AddSource("ConduitRegistration.g.cs", registrationSource);

        // Generate mediator
        var groups = listenerGroups.Select(kvp => new ListenerGroup
        {
            EventFullTypeName = kvp.Key,
            ListenerFullTypeNames = kvp.Value.AsReadOnly(),
        }).ToList();

        var mediatorSource = MediatorEmitter.Emit(dispatchableTypes, handlers, groups);
        context.AddSource("ConduitMediator.g.cs", mediatorSource);
    }

    private static void EmitDiagnostics(
        SourceProductionContext context,
        IReadOnlyList<DispatchableTypeInfo> types,
        IReadOnlyList<HandlerInfo> handlers)
    {
        foreach (var type in types)
        {
            if (type.Kind == DispatchableKind.Event)
            {
                continue;
            }

            var matchingHandlers = handlers.Where(h => h.MessageFullTypeName == type.FullTypeName).ToList();

            if (matchingHandlers.Count == 0)
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
            _ => "IHandler",
        };
    }
}
