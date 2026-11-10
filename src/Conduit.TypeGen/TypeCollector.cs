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
/// A constructed generic (<c>PagedResult&lt;Order&gt;</c>) is collected as its generic definition
/// (<c>PagedResult&lt;T&gt;</c>), once however many instantiations reach it, and its type arguments are walked
/// like any other reached type.
/// </summary>
public static class TypeCollector
{
    /// <summary>
    /// Conduit's own namespaces below the root -- the core and contracts assemblies' (the root <c>Conduit</c> itself is
    /// matched exactly). Each covers its sub-namespaces. Any other <c>Conduit.*</c> namespace is a host's
    /// (<c>Conduit.Sample.Notes</c>) and is walked like any other.
    /// </summary>
    private static readonly string[] ConduitNamespaces =
    [
        "Conduit.Attributes",
        "Conduit.Configuration",
        "Conduit.Exceptions",
        "Conduit.Handlers",
        "Conduit.Messages",
        "Conduit.Middleware",
        "Conduit.Runtime",
    ];

    /// <summary>
    /// Collects all complex types transitively referenced by the given root types.
    /// Returns types not already in the root set that need interfaces generated,
    /// each paired with its immediate base type name when the type is a derived
    /// subtype of an abstract base encountered during the walk.
    /// </summary>
    /// <param name="rootTypeFullNames">The root type full names (already have interfaces).</param>
    /// <param name="additionalSeedTypes">
    /// Additional types to seed the walk (e.g., query result types). A seed is reached like a property type: an array or a
    /// collection reaches its element, and a constructed generic its definition and arguments.
    /// </param>
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
                EnqueueReferencedType(seedType, visited, queue);
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
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // An always-ignored property is never on the wire and MergeFileEmitter leaves it out, so the type behind it
            // (a navigation or service property, say) is not walked either. A conditionally ignored one still is.
            if (JsonMarks.IgnoreOf(property) == JsonIgnoreKind.Always)
            {
                continue;
            }

            EnqueueReferencedType(property.PropertyType, visited, queue);
        }
    }

    /// <summary>
    /// Enqueues the types <paramref name="type"/> reaches that need an interface: an array's, a nullable's or a
    /// collection's element and a dictionary's value (TypeMapper inlines those wrappers as <c>T[]</c>,
    /// <c>T | null</c> and <c>Record&lt;string, T&gt;</c>); any other constructed generic as its DEFINITION -- the one
    /// declaration every instantiation shares (<c>PagedResult&lt;T&gt;</c>) -- and each of its type arguments; and
    /// anything else as itself. A generic parameter (the <c>T</c> inside a definition's own properties) reaches nothing.
    /// </summary>
    private static void EnqueueReferencedType(
        Type type,
        HashSet<string> visited,
        Queue<(Type Type, string? BaseTypeName)> queue)
    {
        if (type.IsGenericParameter)
        {
            return;
        }

        if (type.IsArray)
        {
            EnqueueReferencedType(type.GetElementType()!, visited, queue);
            return;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            var definitionFullName = definition.FullName ?? "";
            var arguments = type.GetGenericArguments();

            if (TypeMapper.IsDictionaryFullName(definitionFullName))
            {
                // The key is always a JSON string (TypeMapper.ToRecordType); only the value reaches a type.
                EnqueueReferencedType(arguments[1], visited, queue);
                return;
            }

            if (definitionFullName != "System.Nullable`1" && !TypeMapper.IsCollectionFullName(definitionFullName))
            {
                TryEnqueue(definition, visited, queue);
            }

            foreach (var argument in arguments)
            {
                EnqueueReferencedType(argument, visited, queue);
            }

            return;
        }

        TryEnqueue(type, visited, queue);
    }

    private static void TryEnqueue(
        Type type,
        HashSet<string> visited,
        Queue<(Type Type, string? BaseTypeName)> queue)
    {
        var fullName = type.FullName;
        if (fullName == null || visited.Contains(fullName) || !NeedsInterface(type))
        {
            return;
        }

        visited.Add(fullName);
        queue.Enqueue((type, null));
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
        if (TypeMapper.IsSimpleType(type))
        {
            return false;
        }

        // Framework types don't get interfaces
        if (IsFrameworkNamespace(type.Namespace ?? ""))
        {
            return false;
        }

        // A closed set is mapped to a string literal union of its values, not an interface
        if (ClosedSetReader.ValuesOf(type) is not null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="ns"/> is a framework's, whose types never get an interface: <c>System</c>,
    /// <c>System.*</c>, <c>Microsoft.*</c>, and Conduit's own namespaces (<see cref="ConduitNamespaces"/>, and the root
    /// <c>Conduit</c>). Bounded by a dot: <c>SystemsInc.*</c>, <c>ConduitApp.*</c> and a host's <c>Conduit.Sample.*</c>
    /// are walked like any other namespace.
    /// </summary>
    internal static bool IsFrameworkNamespace(string ns)
    {
        if (ns == "System"
            || ns.StartsWith("System.", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft.", StringComparison.Ordinal)
            || ns == "Conduit")
        {
            return true;
        }

        foreach (var conduitNamespace in ConduitNamespaces)
        {
            if (ns == conduitNamespace || ns.StartsWith(conduitNamespace + ".", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves a type by full name across multiple assemblies. A C#-style name (<c>Dictionary&lt;K, V&gt;</c>) or an
    /// assembly-qualified one is never a metadata type name, so it resolves to <see langword="null"/> without asking an
    /// assembly -- which would read the part after a comma as an assembly to load.
    /// </summary>
    internal static Type? ResolveType(IReadOnlyList<Assembly> assemblies, string fullName)
    {
        if (fullName.Contains('<', StringComparison.Ordinal) || fullName.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

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
