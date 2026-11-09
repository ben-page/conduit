using System.Collections.Immutable;
using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit.SourceGen.Tests.Support;

public abstract class GeneratorTestBase
{
    /// <summary>
    /// The metadata references shared by the compilation under test and by <see cref="CompileReference(string, string)"/>:
    /// what a project consuming Conduit compiles against -- the runtime, Conduit.Contracts, Conduit and Conduit's own
    /// dependencies (DI, logging, configuration and hosting abstractions) -- so the generated code can be compiled, not
    /// only read.
    /// </summary>
    protected static readonly IReadOnlyList<MetadataReference> BaseReferences = BuildBaseReferences();

    /// <summary>Conduit.Transport.Http, whose presence makes the generator emit <c>ConduitRestEndpoints</c>.</summary>
    protected static readonly MetadataReference HttpTransportReference =
        MetadataReference.CreateFromFile(typeof(Conduit.Transport.Http.ConduitRestHandlers).Assembly.Location);

    /// <summary>Conduit.Resilience, whose presence makes the generator emit <c>ConduitResilienceDispatch</c>.</summary>
    protected static readonly MetadataReference ResilienceReference =
        MetadataReference.CreateFromFile(typeof(Conduit.Resilience.ResilienceBehavior).Assembly.Location);

    /// <summary>
    /// Runs the generator over <paramref name="source"/> (plus <paramref name="extraReferences"/>) and asserts that the
    /// generator did not throw and that the resulting compilation -- the source and everything generated from it --
    /// compiles without errors.
    /// </summary>
    /// <returns>The compilation with the generated sources added, and the generator's diagnostics.</returns>
    protected static (Compilation Output, ImmutableArray<Diagnostic> Generator) RunGenerator(
        string source,
        params MetadataReference[] extraReferences)
        => RunGenerator(source, allowCompileErrors: false, extraReferences);

    /// <summary>
    /// <see cref="RunGenerator(string, MetadataReference[])"/>; with <paramref name="allowCompileErrors"/> the output
    /// compilation's errors are not asserted, for a test whose input is meant not to compile.
    /// </summary>
    protected static (Compilation Output, ImmutableArray<Diagnostic> Generator) RunGenerator(
        string source,
        bool allowCompileErrors,
        params MetadataReference[] extraReferences)
    {
        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            WithoutDuplicates([.. BaseReferences, .. extraReferences]),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver
            .Create(new ConduitGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var output, out var generatorDiagnostics);

        // A generator that throws adds no source, so its output would compile trivially: CS8785 is the throw.
        generatorDiagnostics.Should().NotContain(
            diagnostic => diagnostic.Id == "CS8785", "the generator must not throw");

        if (!allowCompileErrors)
        {
            output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Should().BeEmpty("the source and the code generated from it must compile");
        }

        return (output, generatorDiagnostics);
    }

    /// <summary>
    /// Compiles <paramref name="source"/> into an in-memory assembly and returns it as a
    /// metadata reference, so generator tests can model a handler/contract living in a
    /// *referenced* assembly rather than the compilation under test.
    /// </summary>
    protected static MetadataReference CompileReference(string assemblyName, string source)
        => CompileReference(assemblyName, source, BaseReferences);

    /// <summary>
    /// <see cref="CompileReference(string, string)"/> against an explicit reference set -- e.g. one without
    /// Conduit.Contracts, or with System.Text.Json added.
    /// </summary>
    protected static MetadataReference CompileReference(string assemblyName, string source, IEnumerable<MetadataReference> references)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            WithoutDuplicates(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "Reference compile failed: " + string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        return MetadataReference.CreateFromImage(ms.ToArray());
    }

    /// <summary>The text of the generated file whose hint name contains <paramref name="hintNameContains"/>, or null.</summary>
    protected static string? FindGeneratedSource(Compilation output, string hintNameContains)
    {
        foreach (var tree in output.SyntaxTrees)
        {
            if (tree.FilePath.Contains(hintNameContains, StringComparison.Ordinal))
            {
                return tree.GetText().ToString();
            }
        }

        return null;
    }

    // A test that adds a reference already in the base set (System.Text.Json, say) names the same file twice.
    private static List<MetadataReference> WithoutDuplicates(IEnumerable<MetadataReference> references)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return references
            .Where(reference => reference is not PortableExecutableReference { FilePath: { } path } || files.Add(path))
            .ToList();
    }

    private static System.Collections.ObjectModel.ReadOnlyCollection<MetadataReference> BuildBaseReferences()
    {
        // The test host's trusted platform assemblies are the runtime plus everything this project resolves -- Conduit,
        // Conduit.Contracts and their dependencies among them. The test assembly itself is left out, so nothing it
        // declares is discovered by the generator under test, and so are the two feature assemblies whose presence
        // changes what the generator emits (HttpTransportReference, ResilienceReference): a test opts into each.
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            typeof(GeneratorTestBase).Assembly.Location,
            typeof(Conduit.Transport.Http.ConduitRestHandlers).Assembly.Location,
            typeof(Conduit.Resilience.ResilienceBehavior).Assembly.Location,
        };
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);

        return trustedAssemblies
            .Where(path => !excluded.Contains(path))
            .Order(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList()
            .AsReadOnly();
    }
}
