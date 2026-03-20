namespace Conduit.TypeGen;

/// <summary>
/// Utility methods for TypeScript code generation.
/// </summary>
public static class TypeScriptEmitter
{
    /// <summary>
    /// Resolves a type by full name from the runtime type system.
    /// Finds framework types (System.Guid, etc.) that aren't in the MetadataLoadContext assembly.
    /// Does not find user application types — those must be resolved from the MetadataLoadContext directly.
    /// </summary>
    internal static Type? FindType(string fullTypeName) => Type.GetType(fullTypeName);

    /// <summary>
    /// Converts a PascalCase name to camelCase, handling acronyms correctly.
    /// <c>IPAddress</c> → <c>ipAddress</c>, <c>XMLParser</c> → <c>xmlParser</c>,
    /// <c>Name</c> → <c>name</c>, <c>ID</c> → <c>id</c>.
    /// </summary>
    internal static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        // Find the length of the leading uppercase run
        var upperCount = 0;
        while (upperCount < name.Length && char.IsUpper(name[upperCount]))
        {
            upperCount++;
        }

        if (upperCount == 0)
        {
            return name;
        }

        // Single uppercase letter or all uppercase: lowercase the whole run
        // Multiple uppercase followed by lowercase: lowercase all but last (the last starts the next word)
        var lowercaseCount = upperCount == name.Length || upperCount == 1
            ? upperCount
            : upperCount - 1;

        return string.Concat(
            name[..lowercaseCount].ToLowerInvariant(),
            name[lowercaseCount..]);
    }
}
