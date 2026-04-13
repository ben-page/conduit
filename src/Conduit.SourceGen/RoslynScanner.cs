using System;
using System.Collections.Generic;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen;

/// <summary>
/// Scans a Roslyn <see cref="Compilation"/> for dispatchable types (commands, queries, events)
/// based on namespace declarations and attributes. Used by the source generator at compile time.
/// </summary>
public static class RoslynScanner
{
    private const string CommandInterfaceName = "Conduit.Messages.ICommand";
    private const string CommandWithResultInterfaceName = "Conduit.Messages.ICommand<TResult>";
    private const string QueryInterfaceName = "Conduit.Messages.IQuery<TResult>";
    private const string EventInterfaceName = "Conduit.Messages.IEvent";
    private const string LocalOnlyAttributeName = "Conduit.Attributes.LocalOnlyAttribute";
    private const string FireAndForgetAttributeName = "Conduit.Attributes.FireAndForgetAttribute";
    private const string DoNotAuditAttributeName = "Conduit.Attributes.DoNotAuditAttribute";

    /// <summary>
    /// Scans the compilation for all dispatchable types implementing
    /// <c>ICommand</c>, <c>IQuery</c>, or <c>IEvent</c>.
    /// Types marked with <c>[NotDispatchable]</c> are excluded.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to scan.</param>
    /// <returns>The discovered dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> Scan(Compilation compilation)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var results = new List<DispatchableTypeInfo>();

        foreach (var typeSymbol in GetAllNamedTypes(compilation))
        {
            if (typeSymbol.IsAbstract || (typeSymbol.TypeKind != TypeKind.Class && typeSymbol.TypeKind != TypeKind.Struct))
            {
                continue;
            }

            var kind = DetermineKind(typeSymbol);
            if (kind == null)
            {
                continue;
            }

            if (!ShouldInclude(typeSymbol))
            {
                continue;
            }

            var resultTypeName = GetResultTypeName(typeSymbol, kind.Value);
            var resultFullTypeName = GetResultFullTypeName(typeSymbol, kind.Value);

            results.Add(new DispatchableTypeInfo
            {
                TypeName = typeSymbol.Name,
                FullTypeName = typeSymbol.ToDisplayString(),
                Namespace = typeSymbol.ContainingNamespace.ToDisplayString(),
                Kind = kind.Value,
                ResultTypeName = resultTypeName,
                ResultFullTypeName = resultFullTypeName,
                IsLocalOnly = HasAttribute(typeSymbol, LocalOnlyAttributeName),
                IsFireAndForget = HasAttribute(typeSymbol, FireAndForgetAttributeName),
                IsDoNotAudit = HasAttribute(typeSymbol, DoNotAuditAttributeName),
            });
        }

        return results.AsReadOnly();
    }

    private static DispatchableKind? DetermineKind(INamedTypeSymbol typeSymbol)
    {
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString();

            if (fullName == CommandWithResultInterfaceName)
            {
                return DispatchableKind.Command;
            }

            if (fullName == QueryInterfaceName)
            {
                return DispatchableKind.Query;
            }

            if (fullName == EventInterfaceName)
            {
                return DispatchableKind.Event;
            }
        }

        // Check for ICommand (void) last since ICommand<T> extends ICommand
        foreach (var iface in typeSymbol.AllInterfaces)
        {
            if (iface.ToDisplayString() == CommandInterfaceName)
            {
                return DispatchableKind.VoidCommand;
            }
        }

        return null;
    }

    private static bool ShouldInclude(INamedTypeSymbol typeSymbol)
    {
        // Skip types that aren't accessible to generated code (private nested types, etc.)
        if (typeSymbol.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
        {
            return false;
        }

        // Skip nested types in inaccessible containers
        var container = typeSymbol.ContainingType;
        while (container is not null)
        {
            if (container.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            {
                return false;
            }

            container = container.ContainingType;
        }

        return true;
    }

    private static string? GetResultTypeName(INamedTypeSymbol typeSymbol, DispatchableKind kind)
    {
        if (kind is DispatchableKind.VoidCommand or DispatchableKind.Event)
        {
            return null;
        }

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString();
            if ((kind == DispatchableKind.Command && fullName == CommandWithResultInterfaceName)
                || (kind == DispatchableKind.Query && fullName == QueryInterfaceName))
            {
                if (iface.TypeArguments.Length > 0)
                {
                    return iface.TypeArguments[0].Name;
                }
            }
        }

        return null;
    }

    private static string? GetResultFullTypeName(INamedTypeSymbol typeSymbol, DispatchableKind kind)
    {
        if (kind is DispatchableKind.VoidCommand or DispatchableKind.Event)
        {
            return null;
        }

        foreach (var iface in typeSymbol.AllInterfaces)
        {
            var fullName = iface.OriginalDefinition.ToDisplayString();
            if ((kind == DispatchableKind.Command && fullName == CommandWithResultInterfaceName)
                || (kind == DispatchableKind.Query && fullName == QueryInterfaceName))
            {
                if (iface.TypeArguments.Length > 0)
                {
                    return iface.TypeArguments[0].ToDisplayString();
                }
            }
        }

        return null;
    }

    private static bool HasAttribute(INamedTypeSymbol typeSymbol, string attributeFullName)
    {
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == attributeFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.GlobalNamespace);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            foreach (var member in current.GetMembers())
            {
                if (member is INamedTypeSymbol typeSymbol)
                {
                    yield return typeSymbol;

                    foreach (var nested in GetNestedTypes(typeSymbol))
                    {
                        yield return nested;
                    }
                }
                else if (member is INamespaceSymbol ns)
                {
                    stack.Push(ns);
                }
            }
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetTypeMembers())
        {
            yield return member;

            foreach (var nested in GetNestedTypes(member))
            {
                yield return nested;
            }
        }
    }
}
