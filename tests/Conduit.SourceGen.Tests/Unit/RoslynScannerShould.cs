using AwesomeAssertions;
using Conduit.Analysis;
using Conduit.Messages;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class RoslynScannerShould
{
    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Reference the Conduit assembly
        var conduitAssemblyPath = typeof(ICommand).Assembly.Location;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Guid).Assembly.Location),
            MetadataReference.CreateFromFile(conduitAssemblyPath),
        };

        // Add runtime assemblies needed for compilation
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")));

        return CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    [TestMethod]
    public void FindCommandWithResult()
    {
        var source = @"
using Conduit.Messages;

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
    public void FindVoidCommand()
    {
        var source = @"
using Conduit.Messages;

namespace App.Commands;
public record DeleteOrder(System.Guid Id) : ICommand;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].Kind.Should().Be(DispatchableKind.VoidCommand);
    }

    [TestMethod]
    public void FindQuery()
    {
        var source = @"
using Conduit.Messages;

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
    public void FindEvent()
    {
        var source = @"
using Conduit.Messages;

namespace App.Events;
public record OrderCreated(System.Guid Id) : IEvent;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].Kind.Should().Be(DispatchableKind.Event);
    }

    [TestMethod]
    public void FindTypeInAnyNamespace()
    {
        var source = @"
using Conduit.Messages;

namespace Other;
public record SpecialCommand(string Data) : ICommand;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].TypeName.Should().Be("SpecialCommand");
    }

    [TestMethod]
    public void ExtractResultFullTypeName()
    {
        var source = @"
using Conduit.Messages;

namespace App.Commands;
public record CreateOrder(string CustomerId) : ICommand<System.Guid>;
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types[0].ResultFullTypeName.Should().Be("System.Guid");
    }

    // A real nested dispatchable type whose runtime Type.FullName ("App.Container+NestedCommand")
    // is the canonical wire discriminator / registry key. The scanner must emit the same form.
    private sealed class Container
    {
        public sealed record NestedCommand(string Data) : ICommand;
    }

    [TestMethod]
    public void EmitMetadataStyleFullNameForNestedTypes()
    {
        // The runtime registry (ConduitTypeRegistry) keys on Type.FullName, and the wire
        // discriminator must match it. Type.FullName uses '+' between a nested type and its
        // container, e.g. "Ns.Outer+Inner".
        var source = @"
using Conduit.Messages;

namespace App;
public class Container
{
    public record NestedCommand(string Data) : ICommand;
}
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        types.Should().HaveCount(1);
        types[0].TypeName.Should().Be("NestedCommand");
        types[0].FullTypeName.Should().Be("App.Container+NestedCommand");
    }

    [TestMethod]
    public void MatchRuntimeTypeFullNameForNestedTypes()
    {
        // Parity contract: the scanner's FullTypeName must equal the runtime Type.FullName,
        // which is what ConduitTypeRegistry and ReflectionScanner both use.
        var source = @"
using Conduit.Messages;

namespace App;
public class Container
{
    public record NestedCommand(string Data) : ICommand;
}
";
        var compilation = CreateCompilation(source);
        var types = RoslynScanner.Scan(compilation);

        // The locally-declared Container.NestedCommand proves the runtime form: Type.FullName
        // separates a nested type from its container with '+'. The scanner must match it.
        typeof(Container.NestedCommand).FullName.Should()
            .EndWith("Container+NestedCommand");
        types[0].FullTypeName.Should().EndWith("Container+NestedCommand");
    }
}
