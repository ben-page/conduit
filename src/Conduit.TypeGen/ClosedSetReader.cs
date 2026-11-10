using System.Reflection;
using System.Text;

namespace Conduit.TypeGen;

/// <summary>
/// Reads a closed set (<c>Conduit.Messages.IClosedSet&lt;TSelf&gt;</c> + <c>[ClosedSet(...)]</c>) from metadata. Both
/// are matched by full NAME, never <c>typeof</c>, because TypeGen reflects under a <see cref="MetadataLoadContext"/>,
/// where a <c>typeof</c> comparison is always false. The values come from the attribute because a MetadataLoadContext
/// cannot run the static initializers that create the members (<c>FieldInfo.GetValue</c> throws there -- measured on a
/// closed set loaded under a MetadataLoadContext).
/// </summary>
internal static class ClosedSetReader
{
    /// <summary>The attribute's full name.</summary>
    internal const string AttributeFullName = "Conduit.Attributes.ClosedSetAttribute";

    /// <summary>The interface's open generic full name.</summary>
    internal const string InterfaceFullName = "Conduit.Messages.IClosedSet`1";

    /// <summary>
    /// The wire values <paramref name="type"/> declares as a closed set, in declaration order, or <see langword="null"/>
    /// when it carries no <c>[ClosedSet]</c>. Throws (a hard generation error, as for an interface-name collision) when
    /// it carries the attribute without implementing <c>IClosedSet&lt;itself&gt;</c> -- the runtime would not carry it
    /// as a string, so the union would be false -- or lists no value. The reverse (the interface without the attribute)
    /// and a list that differs from <c>All</c> are the runtime's to catch: TypeGen cannot see <c>All</c>.
    /// </summary>
    internal static IReadOnlyList<string>? ValuesOf(Type type)
    {
        var attribute = type.GetCustomAttributesData()
            .FirstOrDefault(candidate => candidate.AttributeType.FullName == AttributeFullName);
        if (attribute is null)
        {
            return null;
        }

        var implementsItself = type.GetInterfaces().Any(candidate => candidate.IsGenericType
            && candidate.GetGenericTypeDefinition().FullName == InterfaceFullName
            && candidate.GetGenericArguments()[0].FullName == type.FullName);
        if (!implementsItself)
        {
            throw new InvalidOperationException(
                $"Conduit TypeGen: '{type.FullName}' carries [ClosedSet] but does not implement IClosedSet<{type.Name}>, "
                + "so the wire would not carry it as a string. Implement the interface or remove the attribute.");
        }

        // A params string[] argument arrives as ONE constructor argument holding the array's elements.
        var values = new List<string>();
        if (attribute.ConstructorArguments.Count == 1
            && attribute.ConstructorArguments[0].Value is IReadOnlyCollection<CustomAttributeTypedArgument> items)
        {
            foreach (var item in items)
            {
                if (item.Value is not string value)
                {
                    throw new InvalidOperationException(
                        $"Conduit TypeGen: '{type.FullName}' lists a null value in [ClosedSet]; list every member's wire value.");
                }

                values.Add(value);
            }
        }

        if (values.Count == 0)
        {
            throw new InvalidOperationException(
                $"Conduit TypeGen: '{type.FullName}' carries [ClosedSet] with no values; list every member's wire value.");
        }

        return values;
    }

    /// <summary>A single-quoted TypeScript string literal for <paramref name="value"/>, escaped.</summary>
    internal static string ToTsStringLiteral(string value)
    {
        var literal = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    literal.Append(@"\\");
                    break;
                case '\'':
                    literal.Append(@"\'");
                    break;
                case '\n':
                    literal.Append(@"\n");
                    break;
                case '\r':
                    literal.Append(@"\r");
                    break;
                case '\u2028':
                    literal.Append(@"\u2028");
                    break;
                case '\u2029':
                    literal.Append(@"\u2029");
                    break;
                default:
                    literal.Append(character);
                    break;
            }
        }

        return literal.Append('\'').ToString();
    }
}
