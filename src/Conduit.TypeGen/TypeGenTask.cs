using System.Reflection;
using Conduit.Analysis;
using Microsoft.Build.Framework;

namespace Conduit.TypeGen;

public class TypeGenTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public string AssemblyPath { get; set; } = "";

    [Required]
    public string OutputDirectory { get; set; } = "";

    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.Normal,
            $"Conduit TypeGen: scanning {AssemblyPath}, output to {OutputDirectory}");

        try
        {
            var assembly = Assembly.LoadFrom(AssemblyPath);
            var types = ReflectionScanner.Scan(assembly);
            var declaredNamespaces = ReflectionScanner.GetDeclaredNamespaces(assembly);
            var groups = NamespaceGrouper.Group(types, declaredNamespaces);

            Directory.CreateDirectory(OutputDirectory);

            // Emit base file
            File.WriteAllText(
                Path.Combine(OutputDirectory, "conduit.ts"),
                BaseFileEmitter.Emit());

            // Emit per-namespace files
            foreach (var group in groups)
            {
                var content = TypeScriptEmitter.EmitNamespaceFile(group);
                File.WriteAllText(
                    Path.Combine(OutputDirectory, group.FileName),
                    content);
            }

            // Emit merge file
            File.WriteAllText(
                Path.Combine(OutputDirectory, "conduit.generated.ts"),
                MergeFileEmitter.Emit(groups));

            Log.LogMessage(MessageImportance.Normal,
                $"Conduit TypeGen: generated {groups.Count + 2} files");

            return true;
        }
        catch (Exception ex)
        {
            Log.LogError($"Conduit TypeGen failed: {ex.Message}");
            return false;
        }
    }
}
