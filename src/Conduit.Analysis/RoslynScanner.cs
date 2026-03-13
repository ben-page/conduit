using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Conduit.Analysis;

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
    private const string DispatchableAttributeName = "Conduit.Attributes.DispatchableAttribute";
    private const string NotDispatchableAttributeName = "Conduit.Attributes.NotDispatchableAttribute";
    private const string DispatchableNamespaceAttributeName = "Conduit.Attributes.DispatchableNamespaceAttribute";
    private const string AuthorizeAttributeName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
    private const string AllowAnonymousAttributeName = "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute";

    /// <summary>
    /// Extracts namespaces declared via <c>[DispatchableNamespace]</c>
    /// on the assembly.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to inspect.</param>
    /// <returns>The list of declared dispatchable namespaces.</returns>
    public static IReadOnlyList<string> GetDeclaredNamespaces(Compilation compilation)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var namespaces = new List<string>();

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == DispatchableNamespaceAttributeName
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string ns)
            {
                namespaces.Add(ns);
            }
        }

        return namespaces.AsReadOnly();
    }

    /// <summary>
    /// Scans the compilation for all dispatchable types matching the declared namespaces
    /// or annotated with <c>[Dispatchable]</c>.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to scan.</param>
    /// <param name="declaredNamespaces">Optional pre-computed namespaces; retrieved automatically if <see langword="null"/>.</param>
    /// <returns>The discovered dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> Scan(
        Compilation compilation,
        IReadOnlyList<string>? declaredNamespaces = null)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        declaredNamespaces ??= GetDeclaredNamespaces(compilation);
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

            if (!ShouldInclude(typeSymbol, declaredNamespaces))
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
                HasValidateOverride = HasValidateOverride(typeSymbol),
                HasAuthorizeAttribute = HasAttribute(typeSymbol, AuthorizeAttributeName),
                HasAllowAnonymousAttribute = HasAttribute(typeSymbol, AllowAnonymousAttributeName),
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

    private static bool ShouldInclude(INamedTypeSymbol typeSymbol, IReadOnlyList<string> declaredNamespaces)
    {
        // [NotDispatchable] always wins
        if (HasAttribute(typeSymbol, NotDispatchableAttributeName))
        {
            return false;
        }

        // [Dispatchable] always includes
        if (HasAttribute(typeSymbol, DispatchableAttributeName))
        {
            return true;
        }

        // Check if type is in a declared namespace
        var typeNamespace = typeSymbol.ContainingNamespace.ToDisplayString();
        foreach (var ns in declaredNamespaces)
        {
            if (typeNamespace.StartsWith(ns, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    private static bool HasValidateOverride(INamedTypeSymbol typeSymbol)
    {
        foreach (var member in typeSymbol.GetMembers("Validate"))
        {
            if (member is IMethodSymbol method && method.Parameters.Length == 0)
            {
                return true;
            }
        }

        return false;
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
