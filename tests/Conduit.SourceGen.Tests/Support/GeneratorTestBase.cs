using Conduit.Handlers;
using Conduit.Messages;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit.SourceGen.Tests.Support;

public abstract class GeneratorTestBase
{
    /// <summary>
    /// The base metadata references (object/Guid/System.Runtime/Conduit.Contracts/Conduit) shared
    /// by the compilation under test and by <see cref="CompileReference(string, string)"/>.
    /// </summary>
    protected static readonly IReadOnlyList<MetadataReference> BaseReferences = BuildBaseReferences();

    protected static (IReadOnlyList<SyntaxTree> GeneratedTrees, IReadOnlyList<Diagnostic> Diagnostics) RunGenerator(string source)
        => RunGenerator(source, []);

    protected static (IReadOnlyList<SyntaxTree> GeneratedTrees, IReadOnlyList<Diagnostic> Diagnostics) RunGenerator(
        string source,
        params MetadataReference[] extraReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = new List<MetadataReference>(BaseReferences);
        references.AddRange(extraReferences);

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
            references,
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

    private static System.Collections.ObjectModel.ReadOnlyCollection<MetadataReference> BuildBaseReferences()
    {
        // ICommand etc. live in Conduit.Contracts; ICommandHandler<,> etc. live in Conduit
        // core (Handlers/) — both assemblies are needed to resolve the fixtures below.
        var conduitContractsAssemblyPath = typeof(ICommand).Assembly.Location;
        var conduitCoreAssemblyPath = typeof(ICommandHandler<>).Assembly.Location;
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        return new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(conduitContractsAssemblyPath),
            MetadataReference.CreateFromFile(conduitCoreAssemblyPath),
        }.AsReadOnly();
    }
}
