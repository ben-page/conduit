using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Analysis;

/// <summary>
/// Groups dispatchable types by their matching declared namespace for output file organization.
/// </summary>
public static class NamespaceGrouper
{
    /// <summary>
    /// Groups the given types by their best-matching declared namespace.
    /// </summary>
    /// <param name="types">The dispatchable types to group.</param>
    /// <param name="declaredNamespaces">The declared namespaces to match against.</param>
    /// <returns>The grouped types, one group per namespace.</returns>
    public static IReadOnlyList<NamespaceGroup> Group(
        IReadOnlyList<DispatchableTypeInfo> types,
        IReadOnlyList<string> declaredNamespaces)
    {
        if (types is null)
        {
            throw new ArgumentNullException(nameof(types));
        }

        var groups = new Dictionary<string, List<DispatchableTypeInfo>>();

        foreach (var type in types)
        {
            var matchedNamespace = declaredNamespaces
                .Where(ns => type.Namespace.StartsWith(ns, StringComparison.Ordinal))
                .OrderByDescending(ns => ns.Length)
                .FirstOrDefault();

            var groupKey = matchedNamespace ?? "default";

            if (!groups.TryGetValue(groupKey, out var group))
            {
                group = new List<DispatchableTypeInfo>();
                groups[groupKey] = group;
            }

            group.Add(type);
        }

        return groups
            .Select(kvp => new NamespaceGroup
            {
                Namespace = kvp.Key,
                FileName = ToFileName(kvp.Key),
                Types = kvp.Value.AsReadOnly(),
            })
            .OrderBy(g => g.Namespace)
            .ToList()
            .AsReadOnly();
    }

    private static string ToFileName(string ns)
    {
        if (ns == "default")
        {
            return "default.generated.ts";
        }

        var kebab = string.Join("-", ns.Split('.').Select(p => p.ToLowerInvariant()));
        return $"{kebab}.generated.ts";
    }
}
