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
    private const string NotDispatchableAttributeName = "Conduit.Attributes.NotDispatchableAttribute";
    private const string DispatchableNamespaceAttributeName = "Conduit.Attributes.DispatchableNamespaceAttribute";
    private const string RouteAttributeName = "Conduit.Attributes.RouteAttribute";
    private const string RouteNamespaceAttributeName = "Conduit.Attributes.RouteNamespaceAttribute";
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
    /// Extracts namespace-to-endpoint mappings declared via <c>[RouteNamespace]</c>
    /// on the assembly.
    /// </summary>
    /// <param name="compilation">The Roslyn compilation to inspect.</param>
    /// <returns>The list of namespace→endpoint pairs.</returns>
    public static IReadOnlyList<(string Namespace, string EndpointName)> GetRouteNamespaces(Compilation compilation)
    {
        if (compilation is null)
        {
            throw new ArgumentNullException(nameof(compilation));
        }

        var routeNamespaces = new List<(string Namespace, string EndpointName)>();

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == RouteNamespaceAttributeName
                && attribute.ConstructorArguments.Length == 2
                && attribute.ConstructorArguments[0].Value is string ns
                && attribute.ConstructorArguments[1].Value is string endpointName)
            {
                routeNamespaces.Add((ns, endpointName));
            }
        }

        return routeNamespaces.AsReadOnly();
    }

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
        var routeNamespaces = GetRouteNamespaces(compilation);

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
            var routeEndpointName = GetRouteEndpointName(typeSymbol, routeNamespaces);

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
                RouteEndpointName = routeEndpointName,
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
        if (HasAttribute(typeSymbol, NotDispatchableAttributeName))
        {
            return false;
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

    private static string? GetRouteEndpointName(
        INamedTypeSymbol typeSymbol,
        IReadOnlyList<(string Namespace, string EndpointName)> routeNamespaces)
    {
        // Type-level [Route] takes precedence over namespace-level [RouteNamespace]
        foreach (var attribute in typeSymbol.GetAttributes())
        {
            if (attribute.AttributeClass?.ToDisplayString() == RouteAttributeName
                && attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string endpointName)
            {
                return endpointName;
            }
        }

        // Fall back to namespace-level [RouteNamespace]
        var typeNamespace = typeSymbol.ContainingNamespace.ToDisplayString();
        foreach (var (ns, endpointName) in routeNamespaces)
        {
            if (typeNamespace.StartsWith(ns, StringComparison.Ordinal))
            {
                return endpointName;
            }
        }

        return null;
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
