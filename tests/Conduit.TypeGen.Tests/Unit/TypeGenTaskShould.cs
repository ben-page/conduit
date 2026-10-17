using AwesomeAssertions;
using Conduit.TypeGen.Tests.Support;

namespace Conduit.TypeGen.Tests.Unit;

[TestClass]
public class TypeGenTaskShould
{
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
