namespace Conduit.TypeGen.Tests.Support;

/// <summary>
/// Runs <see cref="TypeGenTask"/> -- the real build path: a MetadataLoadContext over the compiled assembly, its referenced
/// application assemblies discovered -- over this test assembly, and returns the <c>conduit.generated.ts</c> it writes.
/// </summary>
public static class TypeGenRun
{
    public static string GenerateForThisAssembly()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var buildEngine = new FakeBuildEngine();
        var task = new TypeGenTask
        {
            AssemblyPath = typeof(TypeGenRun).Assembly.Location,
            OutputDirectory = outputDir,
            Transports = "Http;SignalR",
            BuildEngine = buildEngine,
        };

        try
        {
            if (!task.Execute())
            {
                throw new InvalidOperationException(
                    "TypeGenTask failed over the test assembly: " + string.Join(Environment.NewLine, buildEngine.Errors));
            }

            return File.ReadAllText(Path.Combine(outputDir, "conduit.generated.ts"));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }
}
