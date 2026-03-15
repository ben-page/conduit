using System.Globalization;
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
    public static void EmitMaps(StringBuilder sb, IReadOnlyList<DispatchableTypeInfo> types)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(types);

        var commands = types.Where(t => t.Kind is DispatchableKind.Command or DispatchableKind.VoidCommand).ToList();
        var queries = types.Where(t => t.Kind == DispatchableKind.Query).ToList();
        var events = types.Where(t => t.Kind == DispatchableKind.Event).ToList();

        if (commands.Count > 0)
        {
            sb.AppendLine("export interface CommandMap {");
            foreach (var cmd in commands)
            {
                var resultType = cmd.Kind == DispatchableKind.VoidCommand
                    ? "void"
                    : ResolveResultType(cmd);
                sb.Append(CultureInfo.InvariantCulture,
                    $"  '{cmd.FullTypeName}': {{ request: {cmd.TypeName}; response: {resultType} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (queries.Count > 0)
        {
            sb.AppendLine("export interface QueryMap {");
            foreach (var query in queries)
            {
                var resultType = ResolveResultType(query);
                sb.Append(CultureInfo.InvariantCulture,
                    $"  '{query.FullTypeName}': {{ request: {query.TypeName}; response: {resultType} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
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
        }
    }

    private static string ResolveResultType(DispatchableTypeInfo typeInfo)
    {
        if (typeInfo.ResultFullTypeName is not null)
        {
            var resolvedType = TypeScriptEmitter.FindType(typeInfo.ResultFullTypeName);
            if (resolvedType is not null)
            {
                return TypeMapper.MapType(resolvedType);
            }
        }

        return TypeMapper.MapTypeName(typeInfo.ResultTypeName ?? "unknown");
    }
}
