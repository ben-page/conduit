using System.Reflection;
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

            Directory.CreateDirectory(this.OutputDirectory);

            // Emit base file (transport interfaces)
            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "conduit.ts"),
                BaseFileEmitter.Emit());

            // Emit types file (interfaces + type maps)
            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "default.generated.ts"),
                TypeScriptEmitter.Emit(types));

            // Emit merge file (re-exports + typed mediator)
            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "conduit.generated.ts"),
                MergeFileEmitter.Emit(types));

            this.Log.LogMessage(MessageImportance.Normal,
                "Conduit TypeGen: generated 3 files");

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
