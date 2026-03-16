using System.Reflection;

namespace Conduit.TypeGen;

/// <summary>
/// Maps .NET types to their TypeScript equivalents for code generation.
/// Uses string-based type name comparisons to support both runtime types
/// and <see cref="System.Reflection.MetadataLoadContext"/> types.
/// </summary>
public static class TypeMapper
{
    private static readonly Dictionary<string, string> SimpleTypeMappings = new()
    {
        ["String"] = "string",
        ["Guid"] = "string",
        ["Int32"] = "number",
        ["Int64"] = "number",
        ["Int16"] = "number",
        ["Byte"] = "number",
        ["Decimal"] = "number",
        ["Double"] = "number",
        ["Single"] = "number",
        ["Boolean"] = "boolean",
        ["DateTime"] = "string",
        ["DateTimeOffset"] = "string",
        ["DateOnly"] = "string",
        ["TimeOnly"] = "string",
        ["TimeSpan"] = "string",
    };

    private static readonly HashSet<string> CollectionFullNames =
    [
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
    ];

    private static readonly HashSet<string> DictionaryFullNames =
    [
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
    ];

    /// <summary>
    /// Maps a .NET <see cref="Type"/> to a TypeScript type string.
    /// Works with both runtime types and <see cref="System.Reflection.MetadataLoadContext"/> types.
    /// </summary>
    /// <param name="type">The .NET type to map.</param>
    /// <returns>The corresponding TypeScript type expression.</returns>
    public static string MapType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Handle nullable value types (check by name since Nullable.GetUnderlyingType
        // doesn't work with MetadataLoadContext types)
        if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Nullable`1")
        {
            var underlyingType = type.GetGenericArguments()[0];
            return $"{MapType(underlyingType)} | null";
        }

        // Handle arrays
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            return $"{MapType(elementType)}[]";
        }

        // Handle generic types
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericFullName = genericDef.FullName ?? "";

            // List<T>, IList<T>, IEnumerable<T>, IReadOnlyList<T>, ICollection<T>
            if (CollectionFullNames.Contains(genericFullName))
            {
                var elementType = type.GetGenericArguments()[0];
                return $"{MapType(elementType)}[]";
            }

            // Dictionary<string, T> → Record<string, T>
            if (DictionaryFullNames.Contains(genericFullName))
            {
                var keyType = type.GetGenericArguments()[0];
                var valueType = type.GetGenericArguments()[1];
                return $"Record<{MapType(keyType)}, {MapType(valueType)}>";
            }
        }

        // Handle enums → string literal union
        if (type.IsEnum)
        {
            // Use GetFields instead of Enum.GetNames — works with MetadataLoadContext
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
            var values = fields.Select(f => $"'{f.Name}'");
            return string.Join(" | ", values);
        }

        // Simple type mappings
        if (SimpleTypeMappings.TryGetValue(type.Name, out var tsType))
        {
            return tsType;
        }

        // Object/unknown
        if (type.FullName == "System.Object")
        {
            return "unknown";
        }

        // Complex types - use the type name
        return type.Name;
    }

    /// <summary>
    /// Maps a .NET type name (short name) to a TypeScript type string.
    /// </summary>
    /// <param name="csharpTypeName">The short .NET type name.</param>
    /// <returns>The corresponding TypeScript type, or the original name if no mapping exists.</returns>
    public static string MapTypeName(string csharpTypeName)
    {
        if (SimpleTypeMappings.TryGetValue(csharpTypeName, out var tsType))
        {
            return tsType;
        }

        return csharpTypeName;
    }

    /// <summary>
    /// Maps a fully-qualified C#-style type name (e.g. "System.Collections.Generic.IReadOnlyList&lt;Namespace.NoteDto&gt;")
    /// to a TypeScript type string. Handles generic collection types that cannot be resolved via reflection
    /// because their full name uses C#-style angle brackets instead of CLR backtick notation.
    /// </summary>
    /// <param name="fullTypeName">The fully-qualified C#-style type name.</param>
    /// <returns>The TypeScript type string, or <see langword="null"/> if the type could not be mapped.</returns>
    public static string? MapFullTypeName(string fullTypeName)
    {
        ArgumentNullException.ThrowIfNull(fullTypeName);

        var angleBracketIndex = fullTypeName.IndexOf('<', StringComparison.Ordinal);
        if (angleBracketIndex < 0)
        {
            // Not a generic type — try simple mapping on the short name
            var shortName = GetShortName(fullTypeName);
            if (SimpleTypeMappings.TryGetValue(shortName, out var tsType))
            {
                return tsType;
            }

            return null;
        }

        // Extract outer generic type name and inner type argument(s)
        var outerFullName = fullTypeName[..angleBracketIndex];
        var outerShortName = GetShortName(outerFullName);
        var innerContent = fullTypeName[(angleBracketIndex + 1)..^1]; // strip < and >

        // Collection types → T[]
        if (CollectionShortNames.Contains(outerShortName))
        {
            var elementTs = MapFullTypeName(innerContent);
            if (elementTs is not null)
            {
                return $"{elementTs}[]";
            }

            // Try resolving the element type via reflection
            var elementType = TypeScriptEmitter.FindType(innerContent);
            if (elementType is not null)
            {
                return $"{MapType(elementType)}[]";
            }

            // Last resort: use the short name of the element type
            return $"{GetShortName(innerContent)}[]";
        }

        return null;
    }

    private static string GetShortName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName[(lastDot + 1)..] : fullTypeName;
    }

    private static readonly HashSet<string> CollectionShortNames =
    [
        "List",
        "IList",
        "IEnumerable",
        "IReadOnlyList",
        "ICollection",
        "IReadOnlyCollection",
    ];
}
