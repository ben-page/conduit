using System;
using System.Collections.Generic;
using System.Reflection;

namespace Conduit.Analysis;

/// <summary>
/// Scans a loaded <see cref="Assembly"/> for dispatchable types using reflection.
/// Used at runtime and by the TypeGen MSBuild task.
/// </summary>
public static class ReflectionScanner
{
    private const string CommandInterfaceName = "Conduit.Messages.ICommand";
    private const string CommandWithResultInterfaceName = "Conduit.Messages.ICommand`1";
    private const string QueryInterfaceName = "Conduit.Messages.IQuery`1";
    private const string EventInterfaceName = "Conduit.Messages.IEvent";
    private const string DispatchableAttributeFullName = "Conduit.Attributes.DispatchableAttribute";
    private const string NotDispatchableAttributeFullName = "Conduit.Attributes.NotDispatchableAttribute";
    private const string DispatchableNamespaceAttributeFullName = "Conduit.Attributes.DispatchableNamespaceAttribute";
    private const string AuthorizeAttributeFullName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
    private const string AllowAnonymousAttributeFullName = "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute";

    /// <summary>
    /// Extracts namespaces declared via <c>[DispatchableNamespace]</c> on the assembly.
    /// </summary>
    /// <param name="assembly">The assembly to inspect.</param>
    /// <returns>The list of declared dispatchable namespaces.</returns>
    public static IReadOnlyList<string> GetDeclaredNamespaces(Assembly assembly)
    {
        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        var namespaces = new List<string>();

        foreach (var attribute in assembly.GetCustomAttributes())
        {
            var attrType = attribute.GetType();
            if (attrType.FullName == DispatchableNamespaceAttributeFullName)
            {
                var nsProp = attrType.GetProperty("Namespace");
                if (nsProp?.GetValue(attribute) is string ns)
                {
                    namespaces.Add(ns);
                }
            }
        }

        return namespaces.AsReadOnly();
    }

    /// <summary>
    /// Scans the assembly for dispatchable types using automatically discovered namespaces.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The discovered dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> Scan(Assembly assembly)
    {
        var declaredNamespaces = GetDeclaredNamespaces(assembly);
        return Scan(assembly, declaredNamespaces);
    }

    /// <summary>
    /// Scans the assembly for dispatchable types matching the specified namespaces
    /// or annotated with <c>[Dispatchable]</c>.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <param name="declaredNamespaces">The namespaces to include.</param>
    /// <returns>The discovered dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> Scan(
        Assembly assembly,
        IReadOnlyList<string> declaredNamespaces)
    {
        if (assembly is null)
        {
            throw new ArgumentNullException(nameof(assembly));
        }

        if (declaredNamespaces is null)
        {
            throw new ArgumentNullException(nameof(declaredNamespaces));
        }

        var results = new List<DispatchableTypeInfo>();

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            var kind = DetermineKind(type);
            if (kind == null)
            {
                continue;
            }

            if (!ShouldInclude(type, declaredNamespaces))
            {
                continue;
            }

            results.Add(new DispatchableTypeInfo
            {
                TypeName = type.Name,
                FullTypeName = type.FullName ?? type.Name,
                Namespace = type.Namespace ?? "",
                Kind = kind.Value,
                ResultTypeName = GetResultTypeName(type, kind.Value),
                ResultFullTypeName = GetResultFullTypeName(type, kind.Value),
                HasValidateOverride = HasValidateOverride(type),
                HasAuthorizeAttribute = HasAttributeByName(type, AuthorizeAttributeFullName),
                HasAllowAnonymousAttribute = HasAttributeByName(type, AllowAnonymousAttributeFullName),
            });
        }

        return results.AsReadOnly();
    }

    private static DispatchableKind? DetermineKind(Type type)
    {
        var interfaces = type.GetInterfaces();

        foreach (var iface in interfaces)
        {
            if (iface.IsGenericType)
            {
                var genericDef = iface.GetGenericTypeDefinition();
                if (genericDef.FullName == CommandWithResultInterfaceName)
                {
                    return DispatchableKind.Command;
                }

                if (genericDef.FullName == QueryInterfaceName)
                {
                    return DispatchableKind.Query;
                }
            }

            if (iface.FullName == EventInterfaceName)
            {
                return DispatchableKind.Event;
            }
        }

        // Check for ICommand (void) last since ICommand<T> extends ICommand
        foreach (var iface in interfaces)
        {
            if (iface.FullName == CommandInterfaceName)
            {
                return DispatchableKind.VoidCommand;
            }
        }

        return null;
    }

    private static bool ShouldInclude(Type type, IReadOnlyList<string> declaredNamespaces)
    {
        if (HasAttributeByName(type, NotDispatchableAttributeFullName))
        {
            return false;
        }

        if (HasAttributeByName(type, DispatchableAttributeFullName))
        {
            return true;
        }

        var typeNamespace = type.Namespace ?? "";
        foreach (var ns in declaredNamespaces)
        {
            if (typeNamespace.StartsWith(ns, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetResultTypeName(Type type, DispatchableKind kind)
    {
        if (kind is DispatchableKind.VoidCommand or DispatchableKind.Event)
        {
            return null;
        }

        var targetGenericName = kind == DispatchableKind.Command
            ? CommandWithResultInterfaceName
            : QueryInterfaceName;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition().FullName == targetGenericName)
            {
                return iface.GetGenericArguments()[0].Name;
            }
        }

        return null;
    }

    private static string? GetResultFullTypeName(Type type, DispatchableKind kind)
    {
        if (kind is DispatchableKind.VoidCommand or DispatchableKind.Event)
        {
            return null;
        }

        var targetGenericName = kind == DispatchableKind.Command
            ? CommandWithResultInterfaceName
            : QueryInterfaceName;

        foreach (var iface in type.GetInterfaces())
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition().FullName == targetGenericName)
            {
                return iface.GetGenericArguments()[0].FullName;
            }
        }

        return null;
    }

    private static bool HasValidateOverride(Type type)
    {
        var method = type.GetMethod("Validate", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (method == null)
        {
            return false;
        }

        // Check if it's declared on this type (not just inherited from interface default)
        return method.DeclaringType == type;
    }

    private static bool HasAttributeByName(Type type, string attributeFullName)
    {
        foreach (var attribute in type.GetCustomAttributes(true))
        {
            if (attribute.GetType().FullName == attributeFullName)
            {
                return true;
            }
        }

        return false;
    }
}
