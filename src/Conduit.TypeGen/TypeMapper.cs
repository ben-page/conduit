using System.Reflection;

namespace Conduit.TypeGen;

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

    public static string MapType(Type type)
    {
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
