using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit.SourceGen.Tests;

public abstract class GeneratorTestBase
{
    protected static (IReadOnlyList<SyntaxTree> GeneratedTrees, IReadOnlyList<Diagnostic> Diagnostics) RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var conduitAssemblyPath = typeof(Conduit.ICommand).Assembly.Location;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(conduitAssemblyPath),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new ConduitGenerator();
        var driver = CSharpGeneratorDriver
            .Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        var runResult = driver.GetRunResult();

        var generatedTrees = runResult.GeneratedTrees.ToList().AsReadOnly();
        var allDiagnostics = diagnostics.ToList().AsReadOnly();

        return (generatedTrees, allDiagnostics);
    }

    protected static string? FindGeneratedSource(IReadOnlyList<SyntaxTree> trees, string hintNameContains)
    {
        foreach (var tree in trees)
        {
            if (tree.FilePath.Contains(hintNameContains, StringComparison.Ordinal))
            {
                return tree.GetText().ToString();
            }
        }

        return null;
    }
}
