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

    internal static readonly HashSet<string> CollectionFullNames =
    [
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
    ];

    internal static readonly HashSet<string> DictionaryFullNames =
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
            return $"{ToArrayType(MapType(elementType))}";
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
                return $"{ToArrayType(MapType(elementType))}";
            }

            // Dictionary<string, T> → Record<string, T>
            if (DictionaryFullNames.Contains(genericFullName))
            {
                var keyType = type.GetGenericArguments()[0];
                var valueType = type.GetGenericArguments()[1];
                return $"Record<{MapType(keyType)}, {MapType(valueType)}>";
            }

            // Any other generic type (e.g. PagedResult<T>) → Name<Arg, ...>, arity backtick stripped.
            var typeArguments = type.GetGenericArguments().Select(MapType);
            var genericName = genericDef.Name;
            var tickIndex = genericName.IndexOf('`', StringComparison.Ordinal);
            if (tickIndex >= 0)
            {
                genericName = genericName[..tickIndex];
            }

            return $"{genericName}<{string.Join(", ", typeArguments)}>";
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

        // System.Text.Json dynamic JSON → unknown
        if (type.FullName == "System.Text.Json.JsonElement")
        {
            return "unknown";
        }

        // Complex types - use the type name
        return type.Name;
    }

    /// <summary>
    /// Returns true if the type name maps to a simple TypeScript type (string, number, boolean, etc.).
    /// </summary>
    internal static bool IsSimpleType(string typeName) => SimpleTypeMappings.ContainsKey(typeName);

    /// <summary>
    /// Returns true if the full name represents a collection generic type.
    /// </summary>
    internal static bool IsCollectionFullName(string fullName) => CollectionFullNames.Contains(fullName);

    /// <summary>
    /// Returns true if the full name represents a dictionary generic type.
    /// </summary>
    internal static bool IsDictionaryFullName(string fullName) => DictionaryFullNames.Contains(fullName);

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
                return ToArrayType(elementTs);
            }

            // Try resolving the element type via reflection
            var elementType = TypeScriptEmitter.FindType(innerContent);
            if (elementType is not null)
            {
                return ToArrayType(MapType(elementType));
            }

            // Last resort: use the short name of the element type
            return $"{GetShortName(innerContent)}[]";
        }

        // Dictionary<K, V> → Record<K, V>
        if (DictionaryShortNames.Contains(outerShortName))
        {
            var commaIndex = FindTopLevelComma(innerContent);
            if (commaIndex >= 0)
            {
                var keyContent = innerContent[..commaIndex].Trim();
                var valueContent = innerContent[(commaIndex + 1)..].Trim();
                return $"Record<{MapArg(keyContent)}, {MapArg(valueContent)}>";
            }
        }

        // Any other generic (e.g. PagedResult<Item>) → OuterShort<Arg, ...>, arity-stripped.
        var outerStripped = StripArity(outerShortName);
        var args = SplitTopLevel(innerContent).Select(MapArg);
        return $"{outerStripped}<{string.Join(", ", args)}>";
    }

    private static string GetShortName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName[(lastDot + 1)..] : fullTypeName;
    }

    /// <summary>
    /// Wraps a TypeScript element type in parentheses when necessary before appending <c>[]</c>.
    /// Composite types such as union types (e.g. <c>'A' | 'B' | 'C'</c>) must be parenthesized
    /// so that the array subscript applies to the whole type rather than only the last member.
    /// Atomic types (e.g. <c>string</c>, <c>number</c>, <c>MyDto</c>) are left unparenthesized.
    /// </summary>
    /// <param name="elementTs">The TypeScript type string for the array element.</param>
    /// <returns>The array type string with correct precedence.</returns>
    private static string ToArrayType(string elementTs)
    {
        // A type that contains " | " is a union and must be parenthesized before "[]"
        // so that "[]" binds to the whole union rather than only the last alternative.
        if (elementTs.Contains(" | ", StringComparison.Ordinal))
        {
            return $"({elementTs})[]";
        }

        return $"{elementTs}[]";
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

    private static readonly HashSet<string> DictionaryShortNames =
    [
        "Dictionary",
        "IDictionary",
        "IReadOnlyDictionary",
    ];

    /// <summary>Maps one type-argument string: try the C#-style mapper, then reflection, then the short name.</summary>
    private static string MapArg(string arg)
    {
        var mapped = MapFullTypeName(arg);
        if (mapped is not null)
        {
            return mapped;
        }

        var resolved = TypeScriptEmitter.FindType(arg);
        if (resolved is not null)
        {
            return MapType(resolved);
        }

        return GetShortName(arg);
    }

    private static string StripArity(string name)
    {
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? name[..tick] : name;
    }

    /// <summary>Splits a generic argument list on top-level commas (depth 0 w.r.t. angle brackets).</summary>
    private static List<string> SplitTopLevel(string content)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            switch (content[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(content[start..i].Trim());
                    start = i + 1;
                    break;
                default:
                    break;
            }
        }

        parts.Add(content[start..].Trim());
        return parts;
    }

    private static int FindTopLevelComma(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    return i;
                default:
                    break;
            }
        }

        return -1;
    }
}
