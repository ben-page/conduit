using System.Text;
using Conduit.Analysis;

namespace Conduit.TypeGen;

public static class TypeMapEmitter
{
    public static void EmitMaps(StringBuilder sb, NamespaceGroup group)
    {
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
                sb.AppendLine($"  {cmd.TypeName}: {{ request: {cmd.TypeName}; response: {resultType} }};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (voidCommands.Count > 0)
        {
            sb.AppendLine("export interface VoidCommandMap {");
            foreach (var cmd in voidCommands)
            {
                sb.AppendLine($"  {cmd.TypeName}: {{ request: {cmd.TypeName} }};");
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
                sb.AppendLine($"  {query.TypeName}: {{ request: {query.TypeName}; response: {resultType} }};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        if (events.Count > 0)
        {
            sb.AppendLine("export interface EventMap {");
            foreach (var evt in events)
            {
                sb.AppendLine($"  {evt.TypeName}: {evt.TypeName};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }
    }
}
