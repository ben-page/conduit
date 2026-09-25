using System.Globalization;
using System.Reflection;
using System.Text;
using Conduit.Analysis;

namespace Conduit.TypeGen;

/// <summary>
/// Emits TypeScript type map interfaces (CommandMap, QueryMap, EventMap) that associate
/// request types with their response types for type-safe dispatching.
/// Uses <see cref="DispatchableTypeInfo.FullTypeName"/> as map keys and merges void commands
/// into CommandMap with <c>response: void</c>.
/// </summary>
public static class TypeMapEmitter
{
    /// <summary>
    /// Appends type map interfaces for the given dispatchable types.
    /// </summary>
    /// <param name="sb">The string builder to append to.</param>
    /// <param name="types">The dispatchable types to map.</param>
    /// <param name="assemblies">Optional assemblies for type resolution (may be from MetadataLoadContext).</param>
    /// <returns>
    /// The TypeScript interface names actually emitted this call — a subset of <c>CommandMap</c>,
    /// <c>QueryMap</c>, <c>EventMap</c>, each present only when <paramref name="types"/> has at least one
    /// type of that kind. This is <see cref="MergeFileEmitter"/>'s source of truth for those three names when
    /// it guards against an error type colliding with one of them — derived from the actual emission
    /// decision here rather than a name hand-copied elsewhere, so the two can't drift.
    /// </returns>
    public static IReadOnlyList<string> EmitMaps(StringBuilder sb, IReadOnlyList<DispatchableTypeInfo> types, IReadOnlyList<Assembly>? assemblies = null)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(types);

        var commands = types.Where(t => t.Kind is DispatchableKind.Command or DispatchableKind.VoidCommand).ToList();
        var queries = types.Where(t => t.Kind == DispatchableKind.Query).ToList();
        var events = types.Where(t => t.Kind == DispatchableKind.Event).ToList();
        var emitted = new List<string>();

        if (commands.Count > 0)
        {
            sb.AppendLine("export interface CommandMap {");
            foreach (var cmd in commands)
            {
                var resultType = cmd.Kind == DispatchableKind.VoidCommand
                    ? "void"
                    : ResolveResultType(cmd, assemblies);
                sb.Append(CultureInfo.InvariantCulture,
                    $"  '{cmd.FullTypeName}': {{ request: {cmd.TypeName}; response: {resultType} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
            emitted.Add("CommandMap");
        }

        if (queries.Count > 0)
        {
            sb.AppendLine("export interface QueryMap {");
            foreach (var query in queries)
            {
                var resultType = ResolveResultType(query, assemblies);
                sb.Append(CultureInfo.InvariantCulture,
                    $"  '{query.FullTypeName}': {{ request: {query.TypeName}; response: {resultType} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
            emitted.Add("QueryMap");
        }

        if (events.Count > 0)
        {
            sb.AppendLine("export interface EventMap {");
            foreach (var evt in events)
            {
                sb.Append(CultureInfo.InvariantCulture,
                    $"  '{evt.FullTypeName}': {evt.TypeName};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
            emitted.Add("EventMap");
        }

        return emitted;
    }

    private static string ResolveResultType(DispatchableTypeInfo typeInfo, IReadOnlyList<Assembly>? assemblies)
    {
        if (typeInfo.ResultFullTypeName is not null)
        {
            // Try resolving via assemblies (works with MetadataLoadContext)
            var resolvedType = assemblies != null
                ? TypeCollector.ResolveType(assemblies, typeInfo.ResultFullTypeName)
                : null;
            if (resolvedType is not null)
            {
                return TypeMapper.MapType(resolvedType);
            }

            // Try runtime resolution (works when assembly loaded via LoadFrom)
            resolvedType = TypeScriptEmitter.FindType(typeInfo.ResultFullTypeName);
            if (resolvedType is not null)
            {
                return TypeMapper.MapType(resolvedType);
            }

            // Fall back to string-based mapping for generic types formatted in C#-style
            var mapped = TypeMapper.MapFullTypeName(typeInfo.ResultFullTypeName, assemblies);
            if (mapped is not null)
            {
                return mapped;
            }
        }

        return TypeMapper.MapTypeName(typeInfo.ResultTypeName ?? "unknown");
    }
}
