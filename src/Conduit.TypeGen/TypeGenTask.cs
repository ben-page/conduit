using System.Reflection;
using Conduit.Analysis;
using Microsoft.Build.Framework;

namespace Conduit.TypeGen;

/// <summary>
/// MSBuild task that scans a compiled assembly for dispatchable types and emits
/// TypeScript definitions, type maps, and a typed mediator client.
/// </summary>
public class TypeGenTask : Microsoft.Build.Utilities.Task
{
    /// <summary>
    /// The path to the compiled assembly to scan.
    /// </summary>
    [Required]
    public string AssemblyPath { get; set; } = "";

    /// <summary>
    /// The directory where generated TypeScript files will be written.
    /// </summary>
    [Required]
    public string OutputDirectory { get; set; } = "";

    /// <inheritdoc />
    public override bool Execute()
    {
        this.Log.LogMessage(MessageImportance.Normal,
            $"Conduit TypeGen: scanning {this.AssemblyPath}, output to {this.OutputDirectory}");

        try
        {
            var assembly = Assembly.LoadFrom(this.AssemblyPath);
            var types = ReflectionScanner.ScanClientVisible(assembly);
            var declaredNamespaces = ReflectionScanner.GetDeclaredNamespaces(assembly);
            var groups = NamespaceGrouper.Group(types, declaredNamespaces);

            Directory.CreateDirectory(this.OutputDirectory);

            // Emit base file
            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "conduit.ts"),
                BaseFileEmitter.Emit());

            // Emit per-namespace files
            foreach (var group in groups)
            {
                var content = TypeScriptEmitter.EmitNamespaceFile(group);
                File.WriteAllText(
                    Path.Combine(this.OutputDirectory, group.FileName),
                    content);
            }

            // Emit merge file
            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "conduit.generated.ts"),
                MergeFileEmitter.Emit(groups));

            this.Log.LogMessage(MessageImportance.Normal,
                $"Conduit TypeGen: generated {groups.Count + 2} files");

            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.Log.LogError($"Conduit TypeGen failed: {ex.Message}");
            return false;
        }
    }
}
