namespace Conduit.TypeGen;

/// <summary>
/// Maps .NET types to their TypeScript equivalents for code generation.
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

    /// <summary>
    /// Maps a .NET <see cref="Type"/> to a TypeScript type string.
    /// </summary>
    /// <param name="type">The .NET type to map.</param>
    /// <returns>The corresponding TypeScript type expression.</returns>
    public static string MapType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        // Handle nullable value types
        var underlyingNullable = Nullable.GetUnderlyingType(type);
        if (underlyingNullable != null)
        {
            return $"{MapType(underlyingNullable)} | null";
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
            var genericName = genericDef.Name;

            // List<T>, IList<T>, IEnumerable<T>, IReadOnlyList<T>, ICollection<T>
            if (IsCollectionType(genericDef))
            {
                var elementType = type.GetGenericArguments()[0];
                return $"{MapType(elementType)}[]";
            }

            // Dictionary<string, T> → Record<string, T>
            if (genericDef == typeof(Dictionary<,>) || genericDef == typeof(IDictionary<,>)
                || genericDef == typeof(IReadOnlyDictionary<,>))
            {
                var keyType = type.GetGenericArguments()[0];
                var valueType = type.GetGenericArguments()[1];
                return $"Record<{MapType(keyType)}, {MapType(valueType)}>";
            }
        }

        // Handle enums → string literal union
        if (type.IsEnum)
        {
            var values = Enum.GetNames(type);
            return string.Join(" | ", values.Select(v => $"'{v}'"));
        }

        // Simple type mappings
        if (SimpleTypeMappings.TryGetValue(type.Name, out var tsType))
        {
            return tsType;
        }

        // Object/unknown → unknown
        if (type == typeof(object))
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

    private static bool IsCollectionType(Type genericDef)
    {
        return genericDef == typeof(List<>)
            || genericDef == typeof(IList<>)
            || genericDef == typeof(IEnumerable<>)
            || genericDef == typeof(IReadOnlyList<>)
            || genericDef == typeof(ICollection<>)
            || genericDef == typeof(IReadOnlyCollection<>);
    }
}
