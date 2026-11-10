using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
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
    /// The directory where generated TypeScript files will be written. The targets pass an absolute path, anchored to
    /// <c>$(MSBuildProjectDirectory)</c>: a relative one here resolves against the process's working directory.
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

            var assemblies = this.DiscoverApplicationAssemblies(primaryAssembly, metadataContext);

            var types = ReflectionScanner.ScanClientVisible(assemblies);
            var transports = this.ParseTransports();
            var includeSignalR = transports.Contains("SignalR");

            var outputDir = Path.GetFullPath(this.OutputDirectory);
            Directory.CreateDirectory(outputDir);

            // Normalize to LF so Windows and Linux builds produce identical
            // content — avoids phantom working-tree diffs on consumers whose
            // .gitattributes normalize committed TS to LF.
            var written = new List<string>();
            if (WriteIfChanged(
                Path.Combine(outputDir, "conduit.ts"),
                BaseFileEmitter.Emit(transports, this.BasePath, this.HubPath).ReplaceLineEndings("\n")))
            {
                written.Add("conduit.ts");
            }

            if (WriteIfChanged(
                Path.Combine(outputDir, "conduit.generated.ts"),
                MergeFileEmitter.Emit(types, assemblies, includeSignalR).ReplaceLineEndings("\n")))
            {
                written.Add("conduit.generated.ts");
            }

            this.Log.LogMessage(MessageImportance.Normal, written.Count == 0
                ? $"Conduit TypeGen: {types.Count} types; both files were already current"
                : $"Conduit TypeGen: {types.Count} types; wrote {string.Join(", ", written)}");

            return true;
        }
#pragma warning disable CA1031 // Any failure is reported as a build error: an MSBuild task logs, it does not throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            this.Log.LogError($"Conduit TypeGen failed: {ex.Message}");
            this.Log.LogMessage(MessageImportance.High, ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// Writes <paramref name="content"/> as UTF-8 (no BOM) unless the file already holds exactly those bytes, so a build
    /// that changed nothing leaves the file and its timestamp alone: a dev server watching it does not reload, and a
    /// target with it as an input (the Notes sample's <c>BuildClientApp</c>) stays up to date. Comparing content rather
    /// than declaring <c>Inputs</c>/<c>Outputs</c> on the target keeps the output correct when only the task (whose
    /// embedded transport is <c>conduit.ts</c>) or a <c>ConduitTransports</c>/<c>ConduitBasePath</c>/<c>ConduitHubPath</c>
    /// value changed, none of which an assembly timestamp would show.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    private static bool WriteIfChanged(string path, string content)
    {
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return false;
        }

        File.WriteAllBytes(path, bytes);
        return true;
    }

    /// <summary>
    /// Walks the referenced assembly graph from the primary assembly, loading all
    /// non-framework assemblies. Mirrors the source generator's cross-assembly discovery.
    /// A reference that cannot be loaded (not beside the assembly, nor in the shared framework; a native image) is
    /// skipped, and the build log names it and the reason at Normal importance: its types are not generated.
    /// </summary>
    private List<Assembly> DiscoverApplicationAssemblies(
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
#pragma warning disable CA1031 // any load failure skips that one reference; generation goes on without it
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    this.Log.LogMessage(MessageImportance.Normal,
                        $"Conduit TypeGen: skipped referenced assembly {referencedName.FullName}, which could not be "
                        + $"loaded ({ex.GetType().Name}: {ex.Message}); its types are not generated.");
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Whether a referenced assembly is the framework's, and so not walked: <c>System</c>, <c>System.*</c>,
    /// <c>Microsoft.*</c>, <c>netstandard</c>, <c>mscorlib</c>, <c>WindowsBase</c>. Bounded by a dot, as
    /// <see cref="TypeCollector.IsFrameworkNamespace"/> is: a host's <c>SystemsInc.Contracts</c> is walked.
    /// </summary>
    internal static bool IsFrameworkAssembly(string assemblyName)
    {
        return assemblyName is "System" or "netstandard" or "mscorlib" or "WindowsBase"
            || assemblyName.StartsWith("System.", StringComparison.Ordinal)
            || assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetAssemblyPaths(string assemblyDir, string runtimeDir)
    {
        // Dedupe by simple assembly name so the PathAssemblyResolver never sees
        // two files for the same assembly. On Linux test runs the SDK copies
        // framework DLLs (e.g. Microsoft.Extensions.Hosting.Abstractions.dll)
        // into the app output directory AND they live in the runtime dir, and
        // MetadataLoadContext refuses to load the same assembly twice.
        // Priority: app output (consumer's version wins) > runtime > ASP.NET.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.GetFiles(assemblyDir, "*.dll"))
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(dll)))
            {
                yield return dll;
            }
        }

        foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
        {
            if (seen.Add(Path.GetFileNameWithoutExtension(dll)))
            {
                yield return dll;
            }
        }

        var aspNetDir = FindAspNetCoreSharedFramework(runtimeDir);
        if (aspNetDir != null)
        {
            foreach (var dll in Directory.GetFiles(aspNetDir, "*.dll"))
            {
                if (seen.Add(Path.GetFileNameWithoutExtension(dll)))
                {
                    yield return dll;
                }
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
