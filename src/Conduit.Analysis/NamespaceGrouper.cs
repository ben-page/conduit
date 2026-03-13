using System;
using System.Collections.Generic;
using System.Linq;

namespace Conduit.Analysis;

public static class NamespaceGrouper
{
    public static IReadOnlyList<NamespaceGroup> Group(
        IReadOnlyList<DispatchableTypeInfo> types,
        IReadOnlyList<string> declaredNamespaces)
    {
        var groups = new Dictionary<string, List<DispatchableTypeInfo>>();

        foreach (var type in types)
        {
            var matchedNamespace = declaredNamespaces
                .Where(ns => type.Namespace.StartsWith(ns, StringComparison.Ordinal))
                .OrderByDescending(ns => ns.Length)
                .FirstOrDefault();

            var groupKey = matchedNamespace ?? "default";

            if (!groups.ContainsKey(groupKey))
            {
                groups[groupKey] = new List<DispatchableTypeInfo>();
            }

            groups[groupKey].Add(type);
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
