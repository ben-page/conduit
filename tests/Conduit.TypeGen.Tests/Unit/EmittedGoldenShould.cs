using AwesomeAssertions;
using Conduit.TypeGen.Tests.Support;

namespace Conduit.TypeGen.Tests.Unit;

// The conduit.generated.ts this test assembly's [ClientVisible] types produce, through the real build path (TypeGenTask,
// MetadataLoadContext), is checked in as tests/transport-ts/emitted/conduit.generated.ts next to the default conduit.ts it
// imports, and tests/transport-ts type-checks the pair with strict tsc (CI job transport-ts). So an emitter change that
// alters the output fails here until the golden is regenerated, and one that emits invalid TypeScript fails tsc. Every
// sample type in this project is in it, and Sample/TypeShapeSampleTypes.cs holds the shapes a real host's contracts take.
[TestClass]
public class EmittedGoldenShould
{
    private const string GoldenFileName = "conduit.generated.ts";

    [TestMethod]
    public void MatchTheCheckedInFile()
    {
        // Arrange
        var outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var buildEngine = new FakeBuildEngine();
        var task = new TypeGenTask
        {
            AssemblyPath = typeof(EmittedGoldenShould).Assembly.Location,
            OutputDirectory = outputDir,
            Transports = "Http;SignalR",
            BuildEngine = buildEngine,
        };

        try
        {
            // Act
            var succeeded = task.Execute();

            // Assert
            succeeded.Should().BeTrue(string.Join(Environment.NewLine, buildEngine.Errors));
            var actual = File.ReadAllText(Path.Combine(outputDir, GoldenFileName));
            AssertMatchesGolden(actual);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    private static void AssertMatchesGolden(string actual)
    {
        var goldenPath = Path.Combine(AppContext.BaseDirectory, "emitted", GoldenFileName);
        var golden = File.ReadAllText(goldenPath);
        if (actual.ReplaceLineEndings("\n") == golden.ReplaceLineEndings("\n"))
        {
            return;
        }

        var actualPath = Path.Combine(AppContext.BaseDirectory, "emitted", "conduit.generated.actual.ts");
        File.WriteAllText(actualPath, actual.ReplaceLineEndings("\n"));
        Assert.Fail(
            $"The emitted {GoldenFileName} no longer matches its golden file tests/transport-ts/emitted/{GoldenFileName}. "
            + "The difference is what every consumer's committed conduit.generated.ts will show. If the change is intended, "
            + $"regenerate the golden (cp \"{actualPath}\" tests/transport-ts/emitted/{GoldenFileName}), run `npm test` in "
            + "tests/transport-ts to type-check it, and commit both.");
    }
}
