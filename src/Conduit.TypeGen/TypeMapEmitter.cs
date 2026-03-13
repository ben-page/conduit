using System.Globalization;
using System.Text;
using Conduit.Analysis;

namespace Conduit.TypeGen;

/// <summary>
/// Emits TypeScript type map interfaces (CommandMap, QueryMap, etc.) that associate
/// request types with their response types for type-safe dispatching.
/// </summary>
public static class TypeMapEmitter
{
    /// <summary>
    /// Appends type map interfaces for the given namespace group.
    /// </summary>
    /// <param name="sb">The string builder to append to.</param>
    /// <param name="group">The namespace group containing the dispatchable types.</param>
    public static void EmitMaps(StringBuilder sb, NamespaceGroup group)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(group);

        var commands = group.Types.Where(t => t.Kind == DispatchableKind.Command).ToList();
        var voidCommands = group.Types.Where(t => t.Kind == DispatchableKind.VoidCommand).ToList();
        var queries = group.Types.Where(t => t.Kind == DispatchableKind.Query).ToList();
        var events = group.Types.Where(t => t.Kind == DispatchableKind.Event).ToList();

        if (commands.Count > 0)
        {
            sb.AppendLine("export interface CommandMap {");
            foreach (var cmd in commands)
            {
                var resultType = TypeMapper.MapTypeName(cmd.ResultTypeName ?? "unknown");
                sb.Append(CultureInfo.InvariantCulture, $"  {cmd.TypeName}: {{ request: {cmd.TypeName}; response: {resultType} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (voidCommands.Count > 0)
        {
            sb.AppendLine("export interface VoidCommandMap {");
            foreach (var cmd in voidCommands)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {cmd.TypeName}: {{ request: {cmd.TypeName} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (queries.Count > 0)
        {
            sb.AppendLine("export interface QueryMap {");
            foreach (var query in queries)
            {
                var resultType = TypeMapper.MapTypeName(query.ResultTypeName ?? "unknown");
                sb.Append(CultureInfo.InvariantCulture, $"  {query.TypeName}: {{ request: {query.TypeName}; response: {resultType} }};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (events.Count > 0)
        {
            sb.AppendLine("export interface EventMap {");
            foreach (var evt in events)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {evt.TypeName}: {evt.TypeName};").AppendLine();
            }

            sb.AppendLine("}");
            sb.AppendLine();
        }
    }
}
