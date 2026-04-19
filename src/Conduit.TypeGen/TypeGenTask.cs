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
    /// The base path for Conduit HTTP endpoints. Defaults to "/api/conduit".
    /// The generated TypeScript HttpTransport appends "/dispatch" and "/publish".
    /// </summary>
    public string BasePath { get; set; } = "/api/conduit";

    /// <summary>
    /// The path for the SignalR hub. Defaults to "/conduit".
    /// The generated TypeScript SignalRTransport uses this to build the HubConnection.
    /// </summary>
    public string HubPath { get; set; } = "/conduit";

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
            var assemblyDir = Path.GetDirectoryName(Path.GetFullPath(this.AssemblyPath)) ?? ".";
            var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
            var resolver = new PathAssemblyResolver(
                GetAssemblyPaths(assemblyDir, runtimeDir));

            using var metadataContext = new MetadataLoadContext(resolver);
            var primaryAssembly = metadataContext.LoadFromAssemblyPath(
                Path.GetFullPath(this.AssemblyPath));

            var assemblies = DiscoverApplicationAssemblies(primaryAssembly, metadataContext);

            var types = ReflectionScanner.ScanClientVisible(assemblies);
            var transports = this.ParseTransports();
            var includeSignalR = transports.Contains("SignalR");

            var outputDir = Path.GetFullPath(this.OutputDirectory);
            Directory.CreateDirectory(outputDir);

            // Normalize to LF so Windows and Linux builds produce identical
            // content — avoids phantom working-tree diffs on consumers whose
            // .gitattributes normalize committed TS to LF.
            File.WriteAllText(
                Path.Combine(outputDir, "conduit.ts"),
                BaseFileEmitter.Emit(transports, this.BasePath, this.HubPath).ReplaceLineEndings("\n"));

            File.WriteAllText(
                Path.Combine(outputDir, "conduit.generated.ts"),
                MergeFileEmitter.Emit(types, assemblies, includeSignalR).ReplaceLineEndings("\n"));

            this.Log.LogMessage(MessageImportance.Normal,
                $"Conduit TypeGen: generated 2 files ({types.Count} types)");

            return true;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.Log.LogError($"Conduit TypeGen failed: {ex.Message}");
            this.Log.LogMessage(MessageImportance.High, ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// Walks the referenced assembly graph from the primary assembly, loading all
    /// non-framework assemblies. Mirrors the source generator's cross-assembly discovery.
    /// </summary>
    private static List<Assembly> DiscoverApplicationAssemblies(
        Assembly primaryAssembly, MetadataLoadContext metadataContext)
    {
        var result = new List<Assembly> { primaryAssembly };
        var visited = new HashSet<string> { primaryAssembly.GetName().Name ?? "" };
        var queue = new Queue<Assembly>();
        queue.Enqueue(primaryAssembly);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var referencedName in current.GetReferencedAssemblies())
            {
                var name = referencedName.Name ?? "";
                if (!visited.Add(name))
                {
                    continue;
                }

                if (IsFrameworkAssembly(name))
                {
                    continue;
                }

                try
                {
                    var referencedAssembly = metadataContext.LoadFromAssemblyName(referencedName);
                    result.Add(referencedAssembly);
                    queue.Enqueue(referencedAssembly);
                }
#pragma warning disable CA1031
                catch
#pragma warning restore CA1031
                {
                    // Assembly not loadable — skip (e.g., native dependencies)
                }
            }
        }

        return result;
    }

    private static bool IsFrameworkAssembly(string assemblyName)
    {
        return assemblyName.StartsWith("System", StringComparison.Ordinal)
            || assemblyName.StartsWith("Microsoft", StringComparison.Ordinal)
            || assemblyName.StartsWith("netstandard", StringComparison.Ordinal)
            || assemblyName.StartsWith("mscorlib", StringComparison.Ordinal)
            || assemblyName.StartsWith("WindowsBase", StringComparison.Ordinal);
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
