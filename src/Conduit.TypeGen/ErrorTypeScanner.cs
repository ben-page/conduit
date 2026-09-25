using System.Reflection;

namespace Conduit.TypeGen;

/// <summary>A <c>ConduitException</c> subclass whose declared data reaches the client.</summary>
public sealed record ErrorTypeInfo(string TypeName, string FullTypeName, Type Type, IReadOnlyList<PropertyInfo> Properties);

/// <summary>
/// Finds public, concrete <c>ConduitException</c> subclasses that declare at least one public instance property on
/// themselves or an ancestor BELOW <c>ConduitException</c> — the same walk as
/// <c>ConduitException.ClientDataProperties</c>, re-implemented by full NAME because TypeGen runs under
/// <c>MetadataLoadContext</c>, where <c>typeof</c> comparisons across load contexts are false.
/// </summary>
public static class ErrorTypeScanner
{
    private const string ConduitExceptionFullName = "Conduit.Exceptions.ConduitException";

    /// <summary>
    /// Scans the given assemblies for <c>ConduitException</c> subclasses that declare client data.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan (may be <see cref="System.Reflection.MetadataLoadContext"/>-loaded).</param>
    /// <returns>The discovered error types, ordered by full type name.</returns>
    public static IReadOnlyList<ErrorTypeInfo> Scan(IReadOnlyList<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ErrorTypeInfo>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition || !DerivesFromConduitException(type))
                {
                    continue;
                }

                var properties = DeclaredDataProperties(type);
                var fullName = type.FullName ?? type.Name;
                if (properties.Count > 0 && seen.Add(fullName))
                {
                    result.Add(new ErrorTypeInfo(type.Name, fullName, type, properties));
                }
            }
        }

        return result.OrderBy(e => e.FullTypeName, StringComparer.Ordinal).ToList();
    }

    private static bool DerivesFromConduitException(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.FullName == ConduitExceptionFullName)
            {
                return true;
            }
        }

        return false;
    }

    private static List<PropertyInfo> DeclaredDataProperties(Type type)
    {
        var result = new List<PropertyInfo>();
        for (var t = type; t is not null && t.FullName != ConduitExceptionFullName; t = t.BaseType)
        {
            result.AddRange(t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.GetIndexParameters().Length == 0));
        }

        return result;
    }
}
