using System.Reflection;

namespace Conduit.TypeGen;

/// <summary>
/// A type collected during the property graph walk, paired with the name of its
/// immediate base type when the type was enqueued as a derived subtype of an
/// abstract base discovered during the walk.
/// </summary>
public sealed record CollectedType(Type Type, string? BaseTypeName);

/// <summary>
/// Recursively walks the property type graph of a set of root types,
/// collecting all complex types that need TypeScript interface generation.
/// Skips primitives, collections, dictionaries, enums, closed sets, and framework types.
/// </summary>
public static class TypeCollector
{
    private static readonly HashSet<string> FrameworkNamespacePrefixes =
    [
        "System",
        "Microsoft",
        "Conduit",
    ];

    /// <summary>
    /// Collects all complex types transitively referenced by the given root types.
    /// Returns types not already in the root set that need interfaces generated,
    /// each paired with its immediate base type name when the type is a derived
    /// subtype of an abstract base encountered during the walk.
    /// </summary>
    /// <param name="rootTypeFullNames">The root type full names (already have interfaces).</param>
    /// <param name="additionalSeedTypes">Additional types to seed the walk (e.g., query result types).</param>
    /// <param name="assemblies">The assemblies to resolve types from.</param>
    /// <returns>Additional types that need interface generation, in dependency order.</returns>
    public static IReadOnlyList<CollectedType> CollectReferencedTypes(
        IReadOnlyList<string> rootTypeFullNames,
        IReadOnlyList<Type>? additionalSeedTypes,
        IReadOnlyList<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(rootTypeFullNames);
        ArgumentNullException.ThrowIfNull(assemblies);

        var visited = new HashSet<string>(rootTypeFullNames);
        var result = new List<CollectedType>();
        var queue = new Queue<(Type Type, string? BaseTypeName)>();

        // Seed queue with resolved root types
        foreach (var fullName in rootTypeFullNames)
        {
            var type = ResolveType(assemblies, fullName);
            if (type != null)
            {
                EnqueuePropertyTypes(type, visited, queue);
            }
        }

        // Seed with additional types (e.g., query/command result types)
        if (additionalSeedTypes != null)
        {
            foreach (var seedType in additionalSeedTypes)
            {
                var fullName = seedType.FullName;
                if (fullName != null && visited.Add(fullName) && NeedsInterface(seedType))
                {
                    queue.Enqueue((seedType, null));
                }
            }
        }

        // BFS walk
        while (queue.Count > 0)
        {
            var (type, baseTypeName) = queue.Dequeue();
            result.Add(new CollectedType(type, baseTypeName));
            EnqueuePropertyTypes(type, visited, queue);

            // When an abstract type is reached via the property graph, enumerate its
            // concrete (and abstract intermediate) derived types across the scanned
            // assemblies so they are emitted with the correct `extends` clause.
            if (type.IsAbstract)
            {
                EnqueueDerivedTypes(type, assemblies, visited, queue);
            }
        }

        return result.AsReadOnly();
    }

    private static void EnqueuePropertyTypes(
        Type type,
        HashSet<string> visited,
        Queue<(Type Type, string? BaseTypeName)> queue)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            var propertyType = UnwrapType(property.PropertyType);
            if (propertyType == null)
            {
                continue;
            }

            var fullName = propertyType.FullName;
            if (fullName == null || visited.Contains(fullName))
            {
                continue;
            }

            if (!NeedsInterface(propertyType))
            {
                continue;
            }

            visited.Add(fullName);
            queue.Enqueue((propertyType, null));
        }
    }

    /// <summary>
    /// Enumerates all types derived from <paramref name="baseType"/> across the scanned
    /// assemblies and enqueues each with its immediate parent's short name so that
    /// multi-level chains emit <c>C extends B</c>, <c>B extends A</c> correctly.
    /// Types marked <c>[LocalOnly]</c> (on themselves or a base) and C# interfaces are excluded.
    /// </summary>
    private static void EnqueueDerivedTypes(
        Type baseType,
        IReadOnlyList<Assembly> assemblies,
        HashSet<string> visited,
        Queue<(Type Type, string? BaseTypeName)> queue)
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

                if (ReflectionScanner.IsLocalOnly(candidate))
                {
                    continue;
                }

                var candidateFullName = candidate.FullName ?? candidate.Name;
                if (!visited.Add(candidateFullName))
                {
                    continue;
                }

                // Link to immediate parent so multi-level chains emit `C extends B`, `B extends A`.
                var parentName = candidate.BaseType?.Name;
                queue.Enqueue((candidate, parentName));
            }
        }
    }

    /// <summary>
    /// Unwraps collection/nullable/array types to get the inner element type.
    /// Returns null if the type is a primitive/simple type that doesn't need unwrapping.
    /// </summary>
    private static Type? UnwrapType(Type type)
    {
        // Arrays → element type
        if (type.IsArray)
        {
            return UnwrapType(type.GetElementType()!);
        }

        // Nullable<T> → T
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            return UnwrapType(type.GetGenericArguments()[0]);
        }

        if (type.IsGenericType)
        {
            var genericFullName = type.GetGenericTypeDefinition().FullName ?? "";

            // Collections → element type
            if (TypeMapper.IsCollectionFullName(genericFullName))
            {
                return UnwrapType(type.GetGenericArguments()[0]);
            }

            // Dictionaries → recurse into value type (key is always string/primitive)
            if (TypeMapper.IsDictionaryFullName(genericFullName))
            {
                return UnwrapType(type.GetGenericArguments()[1]);
            }
        }

        return type;
    }

    /// <summary>
    /// Determines if a type needs a TypeScript interface generated for it.
    /// Returns false for C# interfaces, enums, framework types, and types already
    /// handled by TypeMapper. Abstract types are allowed through — they emit a flat
    /// interface and trigger derived-type discovery.
    /// </summary>
    private static bool NeedsInterface(Type type)
    {
        if (type.IsInterface)
        {
            return false;
        }

        // Enums are mapped to string literal unions, not interfaces
        if (type.IsEnum)
        {
            return false;
        }

        // Simple types are mapped directly by TypeMapper
        if (TypeMapper.IsSimpleType(type.Name))
        {
            return false;
        }

        // Framework types don't get interfaces
        var ns = type.Namespace ?? "";
        foreach (var prefix in FrameworkNamespacePrefixes)
        {
            if (ns.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        // A closed set is mapped to a string literal union of its values, not an interface
        if (ClosedSetReader.ValuesOf(type) is not null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a type by full name across multiple assemblies.
    /// </summary>
    internal static Type? ResolveType(IReadOnlyList<Assembly> assemblies, string fullName)
    {
        foreach (var assembly in assemblies)
        {
            var type = assembly.GetType(fullName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
