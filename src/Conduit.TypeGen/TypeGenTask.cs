using System.Reflection;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Semicolon-delimited list of transports to include (e.g. "Http;SignalR").
    /// Defaults to "Http;SignalR".
    /// </summary>
    public string Transports { get; set; } = "Http;SignalR";

    /// <summary>
    /// Parses the <see cref="Transports"/> property into a set of transport names.
    /// </summary>
    public HashSet<string> ParseTransports()
    {
        return new HashSet<string>(
            this.Transports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public override bool Execute()
    {
        this.Log.LogMessage(MessageImportance.Normal,
            $"Conduit TypeGen: scanning {this.AssemblyPath}, output to {this.OutputDirectory}");

        try
        {
            var assemblyDir = Path.GetDirectoryName(this.AssemblyPath) ?? ".";
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var resolver = new PathAssemblyResolver(
                GetAssemblyPaths(assemblyDir, runtimeDir));

            using var metadataContext = new MetadataLoadContext(resolver);
            var assembly = metadataContext.LoadFromAssemblyPath(this.AssemblyPath);

            var types = ReflectionScanner.ScanClientVisible(assembly);
            var transports = this.ParseTransports();
            var includeSignalR = transports.Contains("SignalR");

            Directory.CreateDirectory(this.OutputDirectory);

            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "conduit.ts"),
                BaseFileEmitter.Emit(transports));

            File.WriteAllText(
                Path.Combine(this.OutputDirectory, "conduit.generated.ts"),
                MergeFileEmitter.Emit(types, includeSignalR));

            this.Log.LogMessage(MessageImportance.Normal,
                "Conduit TypeGen: generated 2 files");

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

    private static IEnumerable<string> GetAssemblyPaths(string assemblyDir, string runtimeDir)
    {
        // Include all DLLs from the application output directory
        foreach (var dll in Directory.GetFiles(assemblyDir, "*.dll"))
        {
            yield return dll;
        }

        // Include shared framework assemblies from the runtime directory
        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
        {
            yield return dll;
        }

        // Include ASP.NET Core shared framework if available
        var aspNetDir = FindAspNetCoreSharedFramework(runtimeDir);
        if (aspNetDir != null)
        {
            foreach (var dll in Directory.GetFiles(aspNetDir, "*.dll"))
            {
                yield return dll;
            }
        }
    }

    private static string? FindAspNetCoreSharedFramework(string runtimeDir)
    {
        // runtimeDir is like: /usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.x/
        // ASP.NET Core is at: /usr/lib/dotnet/shared/Microsoft.AspNetCore.App/10.0.x/
        // Normalize trailing separator
        var normalized = runtimeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // normalized = /usr/lib/dotnet/shared/Microsoft.NETCore.App/10.0.x
        var runtimeVersion = Path.GetFileName(normalized);       // 10.0.x
        var frameworkDir = Path.GetDirectoryName(normalized);     // .../Microsoft.NETCore.App
        var sharedDir = Path.GetDirectoryName(frameworkDir);      // .../shared

        if (sharedDir == null || runtimeVersion == null)
        {
            return null;
        }

        var aspNetDir = Path.Combine(sharedDir, "Microsoft.AspNetCore.App", runtimeVersion);

        return Directory.Exists(aspNetDir) ? aspNetDir : null;
    }
}
