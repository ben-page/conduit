using System.Reflection;

namespace Conduit.TypeGen;

/// <summary>A client-typed <c>ConduitException</c> subclass and the client data it declares -- empty for a
/// data-less consumer refusal, which is still typed.</summary>
public sealed record ErrorTypeInfo(string TypeName, string FullTypeName, Type Type, IReadOnlyList<PropertyInfo> Properties);

/// <summary>
/// Finds public, concrete <c>ConduitException</c> subclasses that are CLIENT-TYPED: declared outside
/// Conduit.Contracts (the assembly <c>ConduitException</c> lives in) -- a consumer's refusal, with or without
/// declared data -- or that declare at least one public instance property on themselves or an ancestor BELOW
/// <c>ConduitException</c>, the same walk as <c>ConduitException.ClientDataProperties</c>. This is
/// <c>ConduitException.IsClientTyped</c>'s rule, re-implemented by NAME (assembly name for the "outside
/// Conduit.Contracts" half, full type name for the property walk and dedup) because TypeGen runs under
/// <c>MetadataLoadContext</c>, where <c>typeof</c> comparisons and <see cref="Type.Assembly"/> identity across
/// load contexts are false/distinct.
/// </summary>
public static class ErrorTypeScanner
{
    private const string ConduitExceptionFullName = "Conduit.Exceptions.ConduitException";

    /// <summary>
    /// The assembly name of Conduit.Contracts, where <c>ConduitException</c> and Conduit's built-in status
    /// exceptions live. A subclass declared in any OTHER assembly -- including another Conduit assembly, such as
    /// <c>src/Conduit</c>, were one ever added there -- is a consumer's refusal and is client-typed regardless of
    /// whether it declares data.
    /// </summary>
    private const string ConduitContractsAssemblyName = "Conduit.Contracts";

    /// <summary>
    /// Scans the given assemblies for client-typed <c>ConduitException</c> subclasses.
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
                if ((properties.Count > 0 || !IsConduitOwnAssembly(type)) && seen.Add(fullName))
                {
                    result.Add(new ErrorTypeInfo(type.Name, fullName, type, properties));
                }
            }
        }

        return result.OrderBy(e => e.FullTypeName, StringComparer.Ordinal).ToList();
    }

    private static bool IsConduitOwnAssembly(Type type) =>
        type.Assembly.GetName().Name == ConduitContractsAssemblyName;

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
