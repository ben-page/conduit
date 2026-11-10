using System.Reflection;

namespace Conduit.TypeGen;

/// <summary>
/// Maps .NET types to their TypeScript equivalents for code generation.
/// Uses string-based type name comparisons to support both runtime types
/// and <see cref="System.Reflection.MetadataLoadContext"/> types.
/// </summary>
public static class TypeMapper
{
    /// <summary>
    /// The BCL scalars, by short name. Every one is in the <c>System</c> namespace, and a lookup by <see cref="Type"/> or
    /// by full name checks that, so a host type that shares a short name (<c>MyApp.Uri</c>) is the host's own type.
    /// Each maps to what <c>System.Text.Json</c> writes for it: every integer type, <see cref="Half"/> and the floating
    /// and decimal types are JSON numbers; <see cref="char"/>, <see cref="Uri"/>, <see cref="TimeSpan"/>, <see cref="Guid"/>
    /// and the date and time types are JSON strings.
    /// </summary>
    private static readonly Dictionary<string, string> SimpleTypeMappings = new()
    {
        ["String"] = "string",
        ["Char"] = "string",
        ["Guid"] = "string",
        ["Uri"] = "string",
        ["SByte"] = "number",
        ["Byte"] = "number",
        ["Int16"] = "number",
        ["UInt16"] = "number",
        ["Int32"] = "number",
        ["UInt32"] = "number",
        ["Int64"] = "number",
        ["UInt64"] = "number",
        ["Int128"] = "number",
        ["UInt128"] = "number",
        ["Half"] = "number",
        ["Single"] = "number",
        ["Double"] = "number",
        ["Decimal"] = "number",
        ["Boolean"] = "boolean",
        ["DateTime"] = "string",
        ["DateTimeOffset"] = "string",
        ["DateOnly"] = "string",
        ["TimeOnly"] = "string",
        ["TimeSpan"] = "string",
    };

    /// <summary>
    /// The generic collections <c>System.Text.Json</c> writes as a JSON array, by generic definition full name: they emit
    /// <c>T[]</c>. Lists, sets, queues and stacks, and their immutable, frozen, concurrent and object-model forms.
    /// </summary>
    internal static readonly HashSet<string> CollectionFullNames =
    [
        "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Generic.ICollection`1",
        "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlyList`1",
        "System.Collections.Generic.List`1",
        "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.IReadOnlySet`1",
        "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.SortedSet`1",
        "System.Collections.Generic.LinkedList`1",
        "System.Collections.Generic.Queue`1",
        "System.Collections.Generic.Stack`1",
        "System.Collections.ObjectModel.Collection`1",
        "System.Collections.ObjectModel.ObservableCollection`1",
        "System.Collections.ObjectModel.ReadOnlyCollection`1",
        "System.Collections.ObjectModel.ReadOnlyObservableCollection`1",
        "System.Collections.Immutable.IImmutableList`1",
        "System.Collections.Immutable.ImmutableList`1",
        "System.Collections.Immutable.ImmutableArray`1",
        "System.Collections.Immutable.IImmutableSet`1",
        "System.Collections.Immutable.ImmutableHashSet`1",
        "System.Collections.Immutable.ImmutableSortedSet`1",
        "System.Collections.Immutable.IImmutableQueue`1",
        "System.Collections.Immutable.ImmutableQueue`1",
        "System.Collections.Immutable.IImmutableStack`1",
        "System.Collections.Immutable.ImmutableStack`1",
        "System.Collections.Frozen.FrozenSet`1",
        "System.Collections.Concurrent.ConcurrentBag`1",
        "System.Collections.Concurrent.ConcurrentQueue`1",
        "System.Collections.Concurrent.ConcurrentStack`1",
        "System.Collections.Concurrent.BlockingCollection`1",
    ];

    /// <summary>
    /// The generic dictionaries <c>System.Text.Json</c> writes as a JSON object, by generic definition full name: they emit
    /// <c>Record&lt;string, TValue&gt;</c> (<see cref="ToRecordType"/>).
    /// </summary>
    internal static readonly HashSet<string> DictionaryFullNames =
    [
        "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2",
        "System.Collections.Generic.Dictionary`2",
        "System.Collections.Generic.SortedDictionary`2",
        "System.Collections.Generic.SortedList`2",
        "System.Collections.ObjectModel.ReadOnlyDictionary`2",
        "System.Collections.Immutable.IImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableDictionary`2",
        "System.Collections.Immutable.ImmutableSortedDictionary`2",
        "System.Collections.Frozen.FrozenDictionary`2",
        "System.Collections.Concurrent.ConcurrentDictionary`2",
    ];

    /// <summary>The C# keyword for a BCL type a dictionary key commonly has, for the comment <see cref="ToRecordType"/> writes.</summary>
    private static readonly Dictionary<string, string> CSharpKeywords = new()
    {
        ["System.Boolean"] = "bool",
        ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte",
        ["System.Char"] = "char",
        ["System.Decimal"] = "decimal",
        ["System.Double"] = "double",
        ["System.Single"] = "float",
        ["System.Int32"] = "int",
        ["System.UInt32"] = "uint",
        ["System.Int64"] = "long",
        ["System.UInt64"] = "ulong",
        ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort",
        ["System.Object"] = "object",
    };

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

            // System.Text.Json writes byte[] as a base64 string, not a JSON array of numbers.
            if (elementType.FullName == "System.Byte")
            {
                return "string";
            }

            return $"{ToArrayType(MapType(elementType))}";
        }

        // Handle generic types
        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            var genericFullName = genericDef.FullName ?? "";

            // List<T>, HashSet<T>, ImmutableArray<T>, ... → T[]
            if (CollectionFullNames.Contains(genericFullName))
            {
                var elementType = type.GetGenericArguments()[0];
                return $"{ToArrayType(MapType(elementType))}";
            }

            // Dictionary<TKey, TValue>, SortedDictionary<TKey, TValue>, ... → Record<string, TValue>
            if (DictionaryFullNames.Contains(genericFullName))
            {
                var keyType = type.GetGenericArguments()[0];
                var valueType = type.GetGenericArguments()[1];
                var keyName = keyType.IsGenericType ? keyType.GetGenericTypeDefinition().FullName : keyType.FullName;
                return ToRecordType(keyName ?? keyType.Name, MapType(valueType));
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

        // Handle enums → string literal union of what System.Text.Json writes for each member (ConduitRuntime.JsonOptions:
        // the member's name, or its [JsonStringEnumMemberName]). A [Flags] enum is a string: a combination is written as
        // "Read, Write", which no union of member names covers.
        if (type.IsEnum)
        {
            if (JsonMarks.IsFlags(type))
            {
                return "string";
            }

            // Use GetFields instead of Enum.GetNames — works with MetadataLoadContext
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
            var values = fields.Select(field => ClosedSetReader.ToTsStringLiteral(JsonMarks.EnumMemberName(field)));
            return string.Join(" | ", values);
        }

        // Simple type mappings
        if (IsSimpleType(type))
        {
            return SimpleTypeMappings[type.Name];
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

        // A closed set (Conduit.Messages.IClosedSet<TSelf> + [ClosedSet]) → string literal union of its wire VALUES.
        // Unlike an enum, the wire carries a member as its value (ConduitRuntime.JsonOptions), not its C# name.
        if (ClosedSetReader.ValuesOf(type) is { } closedSetValues)
        {
            return string.Join(" | ", closedSetValues.Select(ClosedSetReader.ToTsStringLiteral));
        }

        // Complex types - use the type name
        return type.Name;
    }

    /// <summary>
    /// Returns true if the type is a BCL scalar that maps to a simple TypeScript type (string, number, boolean).
    /// </summary>
    internal static bool IsSimpleType(Type type) =>
        type.Namespace == "System" && SimpleTypeMappings.ContainsKey(type.Name);

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
    /// because their full name uses C#-style angle brackets instead of CLR backtick notation, and arrays
    /// ("Namespace.NoteDto[]", "Namespace.PagedResult&lt;Namespace.NoteDto&gt;[]").
    /// </summary>
    /// <param name="fullTypeName">The fully-qualified C#-style type name.</param>
    /// <param name="assemblies">
    /// The scanned assemblies (may be <see cref="System.Reflection.MetadataLoadContext"/>-loaded), used to
    /// resolve an element/argument type by name when <see cref="TypeScriptEmitter.FindType"/>'s bare
    /// <c>Type.GetType</c> can't see it -- which is always, for a MetadataLoadContext type. Without this, an
    /// element type this method's short-name table doesn't know (e.g. a project-declared enum) falls back to
    /// its bare name rather than its mapped TypeScript shape (Fix 4).
    /// </param>
    /// <returns>The TypeScript type string, or <see langword="null"/> if the type could not be mapped.</returns>
    public static string? MapFullTypeName(string fullTypeName, IReadOnlyList<Assembly>? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(fullTypeName);

        fullTypeName = fullTypeName.Trim();

        // An array → its element's type, then [] (a byte[] is base64, as in MapType).
        if (fullTypeName.EndsWith(']'))
        {
            var elementName = fullTypeName[..fullTypeName.LastIndexOf('[')];
            return elementName == "System.Byte" ? "string" : ToArrayType(MapArg(elementName, assemblies));
        }

        var angleBracketIndex = fullTypeName.IndexOf('<', StringComparison.Ordinal);
        if (angleBracketIndex < 0)
        {
            // Not a generic type — a BCL scalar by its short name (a bare short name, or one in the System namespace)
            var shortName = GetShortName(fullTypeName);
            var isBcl = shortName.Length == fullTypeName.Length || fullTypeName == $"System.{shortName}";
            if (isBcl && SimpleTypeMappings.TryGetValue(shortName, out var tsType))
            {
                return tsType;
            }

            return null;
        }

        // Extract outer generic type name and inner type argument(s)
        var outerFullName = fullTypeName[..angleBracketIndex];
        var arguments = SplitTopLevel(fullTypeName[(angleBracketIndex + 1)..^1]); // strip < and >
        var definitionFullName = $"{outerFullName}`{arguments.Count}";

        // Nullable<T> -> "T | null", mirroring MapType's reflection branch above. Without this the
        // name falls through to the generic emitter at the end of this method and yields
        // "Nullable<number>" -- a type the emitted client never declares, so the whole file fails to
        // typecheck. The reflection path has always handled this; the string path is what a handler's
        // DECLARED response type goes through, which is why the gap only surfaces for a nullable VALUE
        // type used directly as a response (IQuery<int?>). A nullable REFERENCE type is erased at the
        // CLR level and never reaches here, which is why it went unnoticed.
        if (definitionFullName == "System.Nullable`1")
        {
            return $"{MapArg(arguments[0], assemblies)} | null";
        }

        // Collection types → T[]. The element is tried as a C#-style name, then by reflection (the runtime-loaded
        // search, then the scanned assemblies -- which is what turns a MetadataLoadContext enum into its union rather
        // than its bare name), then as its short name.
        if (CollectionFullNames.Contains(definitionFullName))
        {
            return ToArrayType(MapArg(arguments[0], assemblies));
        }

        // Dictionary<TKey, TValue> → Record<string, TValue>
        if (DictionaryFullNames.Contains(definitionFullName))
        {
            return ToRecordType(arguments[0], MapArg(arguments[1], assemblies));
        }

        // Any other generic (e.g. PagedResult<Item>) → OuterShort<Arg, ...>, arity-stripped.
        var outerStripped = StripArity(GetShortName(outerFullName));
        var args = arguments.Select(a => MapArg(a, assemblies));
        return $"{outerStripped}<{string.Join(", ", args)}>";
    }

    private static string GetShortName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName[(lastDot + 1)..] : fullTypeName;
    }

    /// <summary>
    /// A dictionary's TypeScript type. A JSON object's keys are strings whatever the dictionary's key type
    /// (<c>System.Text.Json</c> writes an <c>int</c> key as <c>"1"</c>, a <c>bool</c> as <c>"True"</c>, an enum as its
    /// member name), and <c>Record&lt;boolean, T&gt;</c> does not compile, so the key is always <c>string</c>. A key that is
    /// not a <see cref="string"/> is named in a comment, so the reader of the generated file still sees it:
    /// <c>Record&lt;string /* int */, T&gt;</c>.
    /// </summary>
    /// <param name="keyFullName">The key type's full name (C#-style or a generic definition's), or a generic parameter's name.</param>
    /// <param name="valueTs">The value's TypeScript type.</param>
    private static string ToRecordType(string keyFullName, string valueTs)
    {
        if (keyFullName == "System.String")
        {
            return $"Record<string, {valueTs}>";
        }

        if (!CSharpKeywords.TryGetValue(keyFullName, out var keyName))
        {
            var angleBracketIndex = keyFullName.IndexOf('<', StringComparison.Ordinal);
            keyName = StripArity(GetShortName(angleBracketIndex < 0 ? keyFullName : keyFullName[..angleBracketIndex]));
            keyName = keyName[(keyName.LastIndexOf('+') + 1)..];
        }

        return $"Record<string /* {keyName} */, {valueTs}>";
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

    /// <summary>Maps one type-argument string: try the C#-style mapper, then reflection, then the short name.</summary>
    private static string MapArg(string arg, IReadOnlyList<Assembly>? assemblies)
    {
        var mapped = MapFullTypeName(arg, assemblies);
        if (mapped is not null)
        {
            return mapped;
        }

        var resolved = TypeScriptEmitter.FindType(arg) ?? ResolveViaAssemblies(arg, assemblies);
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

    /// <summary>
    /// Resolves <paramref name="fullName"/> against <paramref name="assemblies"/> (delegates to
    /// <see cref="TypeCollector.ResolveType"/>). The second element/argument-type resolution attempt in
    /// <see cref="MapFullTypeName"/>/<see cref="MapArg"/>, after <see cref="TypeScriptEmitter.FindType"/>'s
    /// bare <c>Type.GetType</c> — which can never see a <see cref="System.Reflection.MetadataLoadContext"/>
    /// type — returns null. <paramref name="assemblies"/> is <see langword="null"/> when a caller has none
    /// to offer (its own signature has no <c>assemblies</c> parameter to thread through).
    /// </summary>
    private static Type? ResolveViaAssemblies(string fullName, IReadOnlyList<Assembly>? assemblies) =>
        assemblies is null ? null : TypeCollector.ResolveType(assemblies, fullName);

    /// <summary>
    /// Splits a C#-style generic argument list on top-level commas (depth 0 w.r.t. angle brackets, and array brackets:
    /// <c>Item[,]</c> is one argument).
    /// </summary>
    internal static List<string> SplitTopLevel(string content)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            switch (content[i])
            {
                case '<' or '[':
                    depth++;
                    break;
                case '>' or ']':
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
}
