using AwesomeAssertions;
using Conduit.Analysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit.Analysis.Tests;

[TestClass]
public class RoslynScannerTests
{
    private static Compilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Reference the Conduit assembly
        var conduitAssemblyPath = typeof(Conduit.ICommand).Assembly.Location;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(conduitAssemblyPath),
        };

        // Add runtime assemblies needed for compilation
        var runtimeDir = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.Add(MetadataReference.CreateFromFile(System.IO.Path.Combine(runtimeDir, "System.Runtime.dll")));

        return CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [TestMethod]
    public void FindsCommandWithResult()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands;
public record CreateOrder(string CustomerId) : ICommand<System.Guid>;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].TypeName.Should().Be("CreateOrder");
        types[0].Kind.Should().Be(DispatchableKind.Command);
        types[0].ResultTypeName.Should().Be("Guid");
    }

    [TestMethod]
    public void FindsVoidCommand()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands;
public record DeleteOrder(System.Guid Id) : ICommand;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].Kind.Should().Be(DispatchableKind.VoidCommand);
    }

    [TestMethod]
    public void FindsQuery()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Queries;
public record GetOrder(System.Guid Id) : IQuery<string>;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].Kind.Should().Be(DispatchableKind.Query);
        types[0].ResultTypeName.Should().Be("String");
    }

    [TestMethod]
    public void FindsEvent()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Events;
public record OrderCreated(System.Guid Id) : IEvent;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].Kind.Should().Be(DispatchableKind.Event);
    }

    [TestMethod]
    public void NotDispatchable_ExcludesType()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands;

[NotDispatchable]
public record InternalCommand(string Data) : ICommand;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(0);
    }

    [TestMethod]
    public void Dispatchable_IncludesTypeOutsideNamespace()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

namespace Other;

[Dispatchable]
public record SpecialCommand(string Data) : ICommand;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].TypeName.Should().Be("SpecialCommand");
    }

    [TestMethod]
    public void DetectsValidateOverride()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands;
public record ValidatedCommand(string Name) : ICommand
{
    public void Validate()
    {
        if (string.IsNullOrEmpty(Name))
            throw new System.ArgumentException(""Name required"");
    }
}
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].HasValidateOverride.Should().BeTrue();
    }

    [TestMethod]
    public void TypeOutsideNamespace_WithoutDispatchable_IsExcluded()
    {
        var source = @"
using Conduit;

namespace Other;
public record OrphanCommand() : ICommand;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(0);
    }

    [TestMethod]
    public void ExtractsResultFullTypeName()
    {
        var source = @"
using Conduit;
using Conduit.Attributes;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands;
public record CreateOrder(string CustomerId) : ICommand<System.Guid>;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types[0].ResultFullTypeName.Should().Be("System.Guid");
    }
}
