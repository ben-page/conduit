using System.Globalization;
using System.Reflection;

namespace Conduit.TypeGen;

/// <summary>
/// A <c>[JsonPolymorphic]</c> type's discriminator: the key <c>System.Text.Json</c> writes (verbatim -- no naming policy
/// applies to it), per derived type registered with a discriminator its TypeScript literal, and whether a derived type is
/// registered without one (System.Text.Json then writes it with no discriminator).
/// </summary>
internal sealed record JsonPolymorphism(
    string PropertyName,
    IReadOnlyList<(string DerivedFullName, string Literal)> Discriminators,
    bool HasUndiscriminatedDerivedType);

/// <summary>How a <c>[JsonIgnore]</c> on a property leaves it on the wire.</summary>
internal enum JsonIgnoreKind
{
    /// <summary>No <c>[JsonIgnore]</c>, or <c>Condition = Never</c>: the key is always there.</summary>
    None,

    /// <summary><c>[JsonIgnore]</c> (<c>Condition = Always</c>): the key is never there.</summary>
    Always,

    /// <summary>
    /// <c>WhenWritingNull</c>, <c>WhenWritingDefault</c>, <c>WhenWriting</c> or <c>WhenReading</c>: the key is there in
    /// one direction or for some values only, so the TypeScript property is optional.
    /// </summary>
    Conditional,
}

/// <summary>
/// The <c>System.Text.Json</c> marks TypeGen honors, because <c>ConduitRuntime.JsonOptions</c> honors them on the wire:
/// <c>[JsonPropertyName]</c> and <c>[JsonIgnore]</c> on a property, <c>[JsonStringEnumMemberName]</c> on an enum member,
/// <c>[Flags]</c> on an enum, and <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> on a type. Read by attribute full NAME from <see cref="CustomAttributeData"/>, which works under a
/// <see cref="MetadataLoadContext"/> (an attribute instance cannot be created there).
/// </summary>
internal static class JsonMarks
{
    private const string JsonPropertyNameAttributeFullName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
    private const string JsonIgnoreAttributeFullName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    private const string JsonStringEnumMemberNameAttributeFullName = "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute";
    private const string FlagsAttributeFullName = "System.FlagsAttribute";
    private const string JsonPolymorphicAttributeFullName = "System.Text.Json.Serialization.JsonPolymorphicAttribute";
    private const string JsonDerivedTypeAttributeFullName = "System.Text.Json.Serialization.JsonDerivedTypeAttribute";

    // JsonPolymorphicAttribute.TypeDiscriminatorPropertyName's default.
    private const string DefaultTypeDiscriminatorPropertyName = "$type";

    // JsonIgnoreCondition's values: Never = 0, Always = 1 (the attribute's default); the rest are conditional.
    private const int JsonIgnoreConditionNever = 0;
    private const int JsonIgnoreConditionAlways = 1;

    /// <summary>The property's <c>[JsonPropertyName]</c>, or <see langword="null"/> when it has none.</summary>
    internal static string? PropertyName(PropertyInfo property) =>
        FirstConstructorString(property.GetCustomAttributesData(), JsonPropertyNameAttributeFullName);

    /// <summary>How the property's <c>[JsonIgnore]</c>, if any, leaves it on the wire.</summary>
    internal static JsonIgnoreKind IgnoreOf(PropertyInfo property)
    {
        foreach (var attribute in property.GetCustomAttributesData())
        {
            if (attribute.AttributeType.FullName != JsonIgnoreAttributeFullName)
            {
                continue;
            }

            var condition = JsonIgnoreConditionAlways;
            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.MemberName == "Condition" && namedArgument.TypedValue.Value is int value)
                {
                    condition = value;
                }
            }

            return condition switch
            {
                JsonIgnoreConditionNever => JsonIgnoreKind.None,
                JsonIgnoreConditionAlways => JsonIgnoreKind.Always,
                _ => JsonIgnoreKind.Conditional,
            };
        }

        return JsonIgnoreKind.None;
    }

    /// <summary>The name <c>System.Text.Json</c> writes for an enum member: its <c>[JsonStringEnumMemberName]</c>, else its own.</summary>
    internal static string EnumMemberName(FieldInfo field) =>
        FirstConstructorString(field.GetCustomAttributesData(), JsonStringEnumMemberNameAttributeFullName) ?? field.Name;

    /// <summary>
    /// The discriminator <c>System.Text.Json</c> writes for <paramref name="type"/> when it declares
    /// <c>[JsonPolymorphic]</c> or <c>[JsonDerivedType]</c> (either alone makes it polymorphic), or <see langword="null"/>.
    /// A derived type registered without a discriminator is written with none, so it has no entry; a string discriminator
    /// is a quoted literal, an integer one a number.
    /// </summary>
    internal static JsonPolymorphism? PolymorphismOf(Type type)
    {
        string? propertyName = null;
        var polymorphic = false;
        var undiscriminated = false;
        var discriminators = new List<(string, string)>();
        foreach (var attribute in type.GetCustomAttributesData())
        {
            switch (attribute.AttributeType.FullName)
            {
                case JsonPolymorphicAttributeFullName:
                    polymorphic = true;
                    foreach (var namedArgument in attribute.NamedArguments)
                    {
                        if (namedArgument.MemberName == "TypeDiscriminatorPropertyName" && namedArgument.TypedValue.Value is string name)
                        {
                            propertyName = name;
                        }
                    }

                    break;

                case JsonDerivedTypeAttributeFullName:
                    polymorphic = true;
                    undiscriminated |= attribute.ConstructorArguments.Count == 1;
                    if (attribute.ConstructorArguments is [{ Value: Type derived }, { Value: var discriminator }]
                        && derived.FullName is { } derivedFullName)
                    {
                        var literal = discriminator switch
                        {
                            string text => ClosedSetReader.ToTsStringLiteral(text),
                            int number => number.ToString(CultureInfo.InvariantCulture),
                            _ => null,
                        };
                        if (literal is not null)
                        {
                            discriminators.Add((derivedFullName, literal));
                        }
                    }

                    break;

                default:
                    break;
            }
        }

        return polymorphic
            ? new JsonPolymorphism(propertyName ?? DefaultTypeDiscriminatorPropertyName, discriminators, undiscriminated)
            : null;
    }

    /// <summary>Whether the enum is <c>[Flags]</c>.</summary>
    internal static bool IsFlags(Type enumType) =>
        enumType.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName == FlagsAttributeFullName);

    /// <summary>
    /// A TypeScript property key for a wire name: the name itself when it is an identifier, else a quoted string literal
    /// (<c>[JsonPropertyName("x-trace")]</c> → <c>'x-trace'</c>).
    /// </summary>
    internal static string ToTsPropertyKey(string name) =>
        IsIdentifier(name) ? name : ClosedSetReader.ToTsStringLiteral(name);

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] is '_' or '$'))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '_' or '$'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? FirstConstructorString(IList<CustomAttributeData> attributes, string attributeFullName)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.AttributeType.FullName == attributeFullName
                && attribute.ConstructorArguments.Count == 1
                && attribute.ConstructorArguments[0].Value is string value)
            {
                return value;
            }
        }

        return null;
    }
}
