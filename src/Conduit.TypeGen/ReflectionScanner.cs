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
    private const string LocalOnlyAttributeFullName = "Conduit.Attributes.LocalOnlyAttribute";

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
        return ScanClientVisible([assembly]);
    }

    /// <summary>
    /// Scans multiple assemblies for types marked with <c>[ClientVisible]</c>.
    /// Mirrors the source generator's cross-assembly discovery by walking all provided assemblies.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    /// <returns>The discovered client-visible dispatchable types, deduplicated by full type name.</returns>
    public static IReadOnlyList<DispatchableTypeInfo> ScanClientVisible(IReadOnlyList<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var results = new List<DispatchableTypeInfo>();
        var seen = new HashSet<string>();

        foreach (var assembly in assemblies)
        {
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

            // Include dispatchable types that are [ClientVisible]
            foreach (var typeInfo in allTypes)
            {
                if (clientVisibleFullNames.Remove(typeInfo.FullTypeName) && seen.Add(typeInfo.FullTypeName))
                {
                    results.Add(typeInfo);
                }
            }

            // Include non-dispatchable CONCRETE types that are [ClientVisible] (DTOs, models)
            foreach (var type in assemblyTypes)
            {
                var fullName = type.FullName ?? type.Name;
                if (clientVisibleFullNames.Contains(fullName) && !type.IsAbstract && !type.IsInterface && seen.Add(fullName))
                {
                    results.Add(new DispatchableTypeInfo
                    {
                        TypeName = type.Name,
                        FullTypeName = fullName,
                        Namespace = type.Namespace ?? "",
                        Kind = DispatchableKind.Model,
                    });
                }
            }

            // Include non-dispatchable ABSTRACT [ClientVisible] types (base classes/records)
            // and enumerate their concrete derived types in the same assemblies.
            foreach (var type in assemblyTypes)
            {
                var fullName = type.FullName ?? type.Name;
                if (!clientVisibleFullNames.Contains(fullName) || !type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                if (seen.Add(fullName))
                {
                    results.Add(new DispatchableTypeInfo
                    {
                        TypeName = type.Name,
                        FullTypeName = fullName,
                        Namespace = type.Namespace ?? "",
                        Kind = DispatchableKind.Model,
                    });
                }

                AddDerivedTypes(type, assemblies, results, seen);
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

    /// <summary>
    /// Enumerates all derived types of <paramref name="baseType"/> across all scanned assemblies
    /// and appends them to <paramref name="results"/> with their <c>BaseFullTypeName</c> set to
    /// their immediate parent. Abstract intermediates (e.g. B in A &lt;- B &lt;- C) are included
    /// so multi-level chains emit <c>C extends B</c>, <c>B extends A</c> rather than collapsing
    /// all descendants directly under A. Types marked <c>[LocalOnly]</c> are excluded.
    /// </summary>
    private static void AddDerivedTypes(
        Type baseType,
        IReadOnlyList<Assembly> assemblies,
        List<DispatchableTypeInfo> results,
        HashSet<string> seen)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var candidate in assembly.GetExportedTypes())
            {
                if (candidate.IsInterface)
                {
                    continue;
                }

                if (!baseType.IsAssignableFrom(candidate) || candidate == baseType)
                {
                    continue;
                }

                if (HasAttributeByFullName(candidate, LocalOnlyAttributeFullName))
                {
                    continue;
                }

                var candidateFullName = candidate.FullName ?? candidate.Name;
                if (!seen.Add(candidateFullName))
                {
                    continue;
                }

                // Link to immediate parent so multi-level chains emit `C extends B`, `B extends A`.
                var parent = candidate.BaseType;
                var parentFullName = parent?.FullName;
                var parentShortName = parent?.Name;

                results.Add(new DispatchableTypeInfo
                {
                    TypeName = candidate.Name,
                    FullTypeName = candidateFullName,
                    Namespace = candidate.Namespace ?? "",
                    Kind = DispatchableKind.Model,
                    BaseFullTypeName = parentFullName,
                    BaseTypeName = parentShortName,
                });
            }
        }
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
