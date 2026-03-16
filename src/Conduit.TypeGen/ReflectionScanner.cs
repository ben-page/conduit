using System.Reflection;
using Conduit.Analysis;

namespace Conduit.TypeGen;

/// <summary>
/// Scans a loaded <see cref="Assembly"/> for dispatchable types using reflection.
/// Supports both runtime-loaded assemblies and <see cref="System.Reflection.MetadataLoadContext"/>
/// assemblies (where attribute instantiation is unavailable).
/// </summary>
public static class ReflectionScanner
{
    private const string CommandInterfaceName = "Conduit.Messages.ICommand";
    private const string CommandWithResultInterfaceName = "Conduit.Messages.ICommand`1";
    private const string QueryInterfaceName = "Conduit.Messages.IQuery`1";
    private const string EventInterfaceName = "Conduit.Messages.IEvent";
    private const string ClientVisibleAttributeFullName = "Conduit.Attributes.ClientVisibleAttribute";
    private const string AuthorizeAttributeFullName = "Microsoft.AspNetCore.Authorization.AuthorizeAttribute";
    private const string AllowAnonymousAttributeFullName = "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute";

    /// <summary>
    /// Scans the assembly for all dispatchable types implementing
    /// <c>ICommand</c>, <c>IQuery</c>, or <c>IEvent</c>.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The discovered dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> Scan(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

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

            results.Add(new DispatchableTypeInfo
            {
                TypeName = type.Name,
                FullTypeName = type.FullName ?? type.Name,
                Namespace = type.Namespace ?? "",
                Kind = kind.Value,
                ResultTypeName = GetResultTypeName(type, kind.Value),
                ResultFullTypeName = GetResultFullTypeName(type, kind.Value),
                HasValidateOverride = HasValidateOverride(type),
                HasAuthorizeAttribute = HasAttributeByFullName(type, AuthorizeAttributeFullName),
                HasAllowAnonymousAttribute = HasAttributeByFullName(type, AllowAnonymousAttributeFullName),
            });
        }

        return results.AsReadOnly();
    }

    /// <summary>
    /// Scans the assembly for types marked with <c>[ClientVisible]</c>.
    /// Includes dispatchable types (<c>ICommand</c>, <c>IQuery</c>, <c>IEvent</c>) with their
    /// resolved kind, and non-dispatchable types (DTOs, models) as <see cref="DispatchableKind.Model"/>.
    /// Used by TypeGen to emit TypeScript only for types explicitly opted-in to client visibility.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>The discovered client-visible dispatchable types.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> ScanClientVisible(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var allTypes = Scan(assembly);
        var assemblyTypes = assembly.GetExportedTypes();
        var clientVisibleFullNames = new HashSet<string>();

        foreach (var type in assemblyTypes)
        {
            if (HasAttributeByFullName(type, ClientVisibleAttributeFullName))
            {
                var fullName = type.FullName ?? type.Name;
                clientVisibleFullNames.Add(fullName);
            }
        }

        var results = new List<DispatchableTypeInfo>();

        // Include dispatchable types that are [ClientVisible]
        foreach (var typeInfo in allTypes)
        {
            if (clientVisibleFullNames.Contains(typeInfo.FullTypeName))
            {
                results.Add(typeInfo);
                clientVisibleFullNames.Remove(typeInfo.FullTypeName);
            }
        }

        // Include non-dispatchable types that are [ClientVisible] (DTOs, models)
        foreach (var type in assemblyTypes)
        {
            var fullName = type.FullName ?? type.Name;
            if (clientVisibleFullNames.Contains(fullName) && !type.IsAbstract && !type.IsInterface)
            {
                results.Add(new DispatchableTypeInfo
                {
                    TypeName = type.Name,
                    FullTypeName = fullName,
                    Namespace = type.Namespace ?? "",
                    Kind = DispatchableKind.Model,
                    HasValidateOverride = false,
                    HasAuthorizeAttribute = false,
                    HasAllowAnonymousAttribute = false,
                });
            }
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
                return FormatFullTypeName(iface.GetGenericArguments()[0]);
            }
        }

        return null;
    }

    private static string FormatFullTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        // For generic types like List<string>, FullName returns CLR format with backticks
        // and assembly-qualified args. Build the C#-style name instead.
        var genericDef = type.GetGenericTypeDefinition();
        var baseName = (genericDef.FullName ?? genericDef.Name).Split('`')[0];
        var args = string.Join(", ", type.GetGenericArguments().Select(FormatFullTypeName));
        return $"{baseName}<{args}>";
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

    /// <summary>
    /// Checks for an attribute by full type name using <see cref="CustomAttributeData"/>.
    /// Works with both runtime-loaded and <see cref="System.Reflection.MetadataLoadContext"/> assemblies.
    /// </summary>
    private static bool HasAttributeByFullName(Type type, string attributeFullName)
    {
        foreach (var attributeData in type.GetCustomAttributesData())
        {
            if (attributeData.AttributeType.FullName == attributeFullName)
            {
                return true;
            }
        }

        return false;
    }
}
