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

    private static CSharpCompilation CreateCompilation(string source)
        => CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            BaseReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    private static List<IncrementalStepRunReason> RunTwiceAndGetBuildModelReasons(
        Compilation first, Compilation second)
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            generators: [new ConduitGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(
                IncrementalGeneratorOutputKind.None,
                trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(first);
        driver = driver.RunGenerators(second);

        var result = driver.GetRunResult().Results[0];
        return result.TrackedSteps["BuildModel"]
            .SelectMany(step => step.Outputs)
            .Select(output => output.Reason)
            .ToList();
    }
}
