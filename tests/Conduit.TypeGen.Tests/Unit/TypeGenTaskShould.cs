using AwesomeAssertions;
using Conduit.TypeGen.Tests.Support;
using Microsoft.Build.Framework;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class TypeGenTaskShould
{
    // A referenced, non-framework assembly that no exported type of this test assembly names in a signature: only
    // method bodies use it, so the scan succeeds without it.
    private const string ReferencedOnlyFromMethodBodies = "AwesomeAssertions";

    [TestMethod]
    public void LogWhenAReferencedAssemblyFailsToLoad()
    {
        // Arrange: this test assembly in a directory of its own, beside every DLL of its output but one it references.
        var thisAssembly = typeof(TypeGenTaskShould).Assembly.Location;
        var scanDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(scanDir);
        foreach (var dll in Directory.GetFiles(Path.GetDirectoryName(thisAssembly)!, "*.dll"))
        {
            if (Path.GetFileNameWithoutExtension(dll) != ReferencedOnlyFromMethodBodies)
            {
                File.Copy(dll, Path.Combine(scanDir, Path.GetFileName(dll)));
            }
        }

        var buildEngine = new FakeBuildEngine();
        var task = new TypeGenTask
        {
            AssemblyPath = Path.Combine(scanDir, Path.GetFileName(thisAssembly)),
            OutputDirectory = Path.Combine(scanDir, "generated"),
            BuildEngine = buildEngine,
        };

        try
        {
            // Act
            var succeeded = task.Execute();

            // Assert: generation goes on without it, and the build log says which assembly was skipped and why.
            succeeded.Should().BeTrue(string.Join(Environment.NewLine, buildEngine.Errors));
            var logged = buildEngine.Messages.Should()
                .ContainSingle(message => message.Message!.Contains(ReferencedOnlyFromMethodBodies, StringComparison.Ordinal))
                .Subject;
            logged.Importance.Should().Be(MessageImportance.Normal);
            logged.Message.Should().Contain(nameof(FileNotFoundException), "the reason is named");
        }
        finally
        {
            Directory.Delete(scanDir, true);
        }
    }

    [TestMethod]
    public void RewriteOnlyAFileWhoseContentChanged()
    {
        // Arrange: a generated pair, both stamped long ago, one of them no longer what the task writes.
        var outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var task = new TypeGenTask
        {
            AssemblyPath = typeof(TypeGenTaskShould).Assembly.Location,
            OutputDirectory = outputDir,
            BuildEngine = new FakeBuildEngine(),
        };
        var runtimeFile = Path.Combine(outputDir, "conduit.ts");
        var clientFile = Path.Combine(outputDir, "conduit.generated.ts");
        var longAgo = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            task.Execute().Should().BeTrue();
            var runtime = File.ReadAllText(runtimeFile);
            File.WriteAllText(runtimeFile, "// stale");
            File.SetLastWriteTimeUtc(runtimeFile, longAgo);
            File.SetLastWriteTimeUtc(clientFile, longAgo);

            // Act
            task.Execute().Should().BeTrue();

            // Assert: an unchanged file keeps its timestamp, so a watcher or a target with it as an input stays quiet.
            File.ReadAllText(runtimeFile).Should().Be(runtime);
            File.GetLastWriteTimeUtc(runtimeFile).Should().BeAfter(longAgo);
            File.GetLastWriteTimeUtc(clientFile).Should().Be(longAgo);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }
    [TestMethod]
    public void ParseTransportsProperty()
    {
        // Arrange
        var task = new TypeGenTask { Transports = "Http;SignalR" };

        // Act
        var transports = task.ParseTransports();

        // Assert
        transports.Should().Contain("Http");
        transports.Should().Contain("SignalR");
    }

    [TestMethod]
    public void ParseHttpOnlyTransport()
    {
        // Arrange
        var task = new TypeGenTask { Transports = "Http" };

        // Act
        var transports = task.ParseTransports();

        // Assert
        transports.Should().Contain("Http");
        transports.Should().NotContain("SignalR");
    }

    [TestMethod]
    public void DefaultTransportsToHttpAndSignalR()
    {
        // Arrange
        var task = new TypeGenTask();

        // Act
        var transports = task.ParseTransports();

        // Assert
        transports.Should().Contain("Http");
        transports.Should().Contain("SignalR");
    }

    [TestMethod]
    public void WriteTwoOutputFiles()
    {
        // Arrange
        var outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var task = new TypeGenTask
        {
            AssemblyPath = typeof(TypeGenTaskShould).Assembly.Location,
            OutputDirectory = outputDir,
            Transports = "Http;SignalR",
            BuildEngine = new FakeBuildEngine(),
        };

        try
        {
            // Act
            task.Execute();

            // Assert
            File.Exists(Path.Combine(outputDir, "conduit.ts")).Should().BeTrue();
            File.Exists(Path.Combine(outputDir, "conduit.generated.ts")).Should().BeTrue();
            File.Exists(Path.Combine(outputDir, "default.generated.ts")).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [TestMethod]
    public void Emits_a_nullable_result_from_the_compiled_assembly()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var task = new TypeGenTask
        {
            AssemblyPath = typeof(TypeGenTaskShould).Assembly.Location,
            OutputDirectory = outputDir,
            Transports = "Http;SignalR",
            BuildEngine = new FakeBuildEngine(),
        };

        try
        {
            task.Execute();

            var lines = File.ReadAllLines(Path.Combine(outputDir, "conduit.generated.ts"));
            lines.Single(l => l.Contains("TestFindQuery'", StringComparison.Ordinal)).TrimEnd().Should().EndWith("response: TestFound | null };");
            lines.Single(l => l.Contains("TestRequireQuery'", StringComparison.Ordinal)).TrimEnd().Should().EndWith("response: TestFound };");
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
