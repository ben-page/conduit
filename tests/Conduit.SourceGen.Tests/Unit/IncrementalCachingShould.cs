using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit.SourceGen.Tests.Unit;

/// <summary>
/// Proves the value-equatable model lets Roslyn cache the emission: a model-irrelevant edit leaves the
/// BuildModel step Unchanged (so re-emission is skipped), while adding a real dispatchable type marks it
/// Modified (so the cache correctly invalidates).
/// </summary>
[TestClass]
public class IncrementalCachingShould : GeneratorTestBase
{
    private const string Source = """
        using Conduit.Messages;
        using Conduit.Handlers;
        using System.Threading;
        using System.Threading.Tasks;

        namespace App;

        public record CreateThing(string Name) : ICommand;

        public class CreateThingHandler : ICommandHandler<CreateThing>
        {
            public Task HandleAsync(CreateThing command, CancellationToken cancellationToken) => Task.CompletedTask;
        }
        """;

    [TestMethod]
    public void CacheBuildModelWhenEditIsModelIrrelevant()
    {
        var compilation = CreateCompilation(Source);
        // A comment-only edit: the Compilation changes, but discovery (commands/handlers) does not.
        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.First(),
            CSharpSyntaxTree.ParseText(Source + "\n// a model-irrelevant comment\n"));

        var reasons = RunTwiceAndGetBuildModelReasons(compilation, edited);

        reasons.Should().NotBeEmpty();
        reasons.Should().AllSatisfy(reason =>
            reason.Should().Be(IncrementalStepRunReason.Unchanged));
    }

    [TestMethod]
    public void InvalidateBuildModelWhenADispatchableTypeIsAdded()
    {
        var compilation = CreateCompilation(Source);
        var edited = compilation.ReplaceSyntaxTree(
            compilation.SyntaxTrees.First(),
            CSharpSyntaxTree.ParseText(Source + "\npublic record AnotherCommand : Conduit.Messages.ICommand;\n"));

        var reasons = RunTwiceAndGetBuildModelReasons(compilation, edited);

        reasons.Should().Contain(reason =>
            reason == IncrementalStepRunReason.Modified || reason == IncrementalStepRunReason.New);
    }

    [TestMethod]
    public void KeepTheEmissionCachedWhenAnEditMovesADeclaration()
    {
        // Arrange -- a command with no handler, so a diagnostic points at it; the edit inserts a line at the top of the
        // file, which moves every declaration's span and nothing else
        const string source = Source + "\npublic record ShipThing(string Name) : ICommand;\n";
        const string editedSource = "// a line inserted at the top\n" + source;
        var compilation = CreateCompilation(source);
        var edited = compilation.ReplaceSyntaxTree(compilation.SyntaxTrees.First(), CSharpSyntaxTree.ParseText(editedSource));

        // Act
        var result = RunTwice(compilation, edited);

        // Assert -- the walk saw the move, the emission's input did not
        Reasons(result, "BuildModel").Should().Contain(IncrementalStepRunReason.Modified);
        Reasons(result, "EmissionModel").Should().NotBeEmpty()
            .And.AllSatisfy(reason => reason.Should().Be(IncrementalStepRunReason.Unchanged));

        // Assert -- the diagnostic still follows the declaration to its new line
        var expectedLine = editedSource[..editedSource.IndexOf("ShipThing", StringComparison.Ordinal)].Count(character => character == '\n');
        result.Diagnostics.Single(diagnostic => diagnostic.Id == "CONDUIT001")
            .Location.GetLineSpan().StartLinePosition.Line.Should().Be(expectedLine);
    }

    private static CSharpCompilation CreateCompilation(string source)
        => CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static List<IncrementalStepRunReason> RunTwiceAndGetBuildModelReasons(
        Compilation first, Compilation second)
        => Reasons(RunTwice(first, second), "BuildModel");

    // The generator's result for the second of two runs, with its steps tracked.
    private static GeneratorRunResult RunTwice(Compilation first, Compilation second)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ConduitGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(first);
        driver = driver.RunGenerators(second);

        return driver.GetRunResult().Results[0];
    }

    private static List<IncrementalStepRunReason> Reasons(GeneratorRunResult result, string trackingName)
        => result.TrackedSteps[trackingName]
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToList();
}
