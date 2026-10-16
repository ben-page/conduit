using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen.Tests.Unit;

/// <summary>
/// CONDUIT006 checks [AuditAs] on every type a message carries -- reached through its members' types, nullables, arrays
/// and generic arguments, within the compilation -- not only on the message itself.
/// </summary>
[TestClass]
public class CarriedAuditAsDiagnosticShould : GeneratorTestBase
{
    private static readonly MetadataReference JsonReference = MetadataReference.CreateFromFile(typeof(JsonElement).Assembly.Location);

    private static List<string> AuditAsMessages(string source, params MetadataReference[] extraReferences)
    {
        var (_, diagnostics) = RunGenerator(source, [JsonReference, .. extraReferences]);
        return diagnostics.Where(d => d.Id == "CONDUIT006").Select(d => d.GetMessage(CultureInfo.InvariantCulture)).ToList();
    }

    [TestMethod]
    public void NotReportAWellFormedCarriedType()
    {
        // Arrange
        var source = @"
using System.Collections.Generic;
using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record BatchItem(string Type, [AuditAs(nameof(Type))] JsonElement Payload);

    public record BatchCommand(IReadOnlyList<BatchItem> Items) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().BeEmpty();
    }

    [TestMethod]
    public void ReportACarriedMemberThatIsNotAJsonElement()
    {
        // Arrange
        var source = @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record BatchItem(string Type, [AuditAs(nameof(Type))] string Payload);

    public record BatchCommand(IReadOnlyList<BatchItem> Items) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().ContainSingle()
            .Which.Should().Contain("'App.Commands.BatchItem'").And.Contain("Payload is not a JsonElement");
    }

    [TestMethod]
    public void ReportACarriedSiblingThatDoesNotExist()
    {
        // Arrange
        var source = @"
using System.Collections.Generic;
using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record BatchItem(string Type, [AuditAs(""Kind"")] JsonElement Payload);

    public record BatchCommand(IReadOnlyList<BatchItem> Items) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().ContainSingle()
            .Which.Should().Contain("'App.Commands.BatchItem'").And.Contain("'Kind', which is not a property");
    }

    [TestMethod]
    public void ReportACarriedSiblingThatIsNotAString()
    {
        // Arrange
        var source = @"
using System.Collections.Generic;
using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public sealed class BatchItem
    {
        public int Kind { get; init; }

        [AuditAs(nameof(Kind))]
        public JsonElement? Payload { get; init; }
    }

    public record BatchCommand(IReadOnlyList<BatchItem> Items) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().ContainSingle()
            .Which.Should().Contain("'App.Commands.BatchItem'").And.Contain("'Kind', which is not a string");
    }

    [TestMethod]
    public void ReportCarriedTypesReachedThroughNullablesArraysAndGenericArguments()
    {
        // Arrange -- each misapplied type is reached one way only, from properties and record parameters alike
        var source = @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Queries
{
    public struct ByNullable { public string Type { get; init; } [AuditAs(nameof(Type))] public string Payload { get; init; } }

    public record ByArray(string Type, [AuditAs(nameof(Type))] string Payload);

    public record ByGenericArgument(string Type, [AuditAs(nameof(Type))] string Payload);

    public record Holder(ByNullable? Nullable, ByArray[] Array);

    public sealed class Lookup : IQuery<int>
    {
        public Holder? Holder { get; init; }

        public Dictionary<string, List<ByGenericArgument>> ByKey { get; init; } = new();
    }
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().HaveCount(3);
        messages.Should().ContainSingle(message => message.Contains("'App.Queries.ByNullable'"));
        messages.Should().ContainSingle(message => message.Contains("'App.Queries.ByArray'"));
        messages.Should().ContainSingle(message => message.Contains("'App.Queries.ByGenericArgument'"));
    }

    [TestMethod]
    public void NotHangOnACycle()
    {
        // Arrange -- A carries B carries A, and a type that carries itself
        var source = @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Events
{
    public record NodeA(string Type, NodeB? Next);

    public record NodeB(string Type, [AuditAs(nameof(Type))] string Payload, NodeA? Back, List<NodeB> Children);

    public record Changed(NodeA Root) : IEvent;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().ContainSingle().Which.Should().Contain("'App.Events.NodeB'");
    }

    [TestMethod]
    public void ReportATypeReachedFromTwoMessagesOnce()
    {
        // Arrange -- two messages carry the same item; a third message is itself carried, and is reported as a message
        var source = @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record BatchItem(string Type, [AuditAs(nameof(Type))] string Payload);

    public record Inner(string Type, [AuditAs(nameof(Type))] string Payload) : IEvent;

    public record FirstBatch(IReadOnlyList<BatchItem> Items, Inner Nested) : ICommand;

    public record SecondBatch(BatchItem[] Items, Inner? Nested) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().HaveCount(2);
        messages.Should().ContainSingle(message => message.Contains("'App.Commands.BatchItem'"));
        messages.Should().ContainSingle(message => message.Contains("'App.Commands.Inner'"));
    }

    [TestMethod]
    public void ReportAMisappliedCarriedTypeFromAReferencedAssembly()
    {
        // Arrange -- contracts compiled in a library the generator does not run on, as a host references them
        var itemReference = CompileReference("Items", @"
using Conduit.Attributes;

namespace Items
{
    public record ForeignItem(string Type, [AuditAs(nameof(Type))] string Payload);
}
");
        var source = @"
using System.Collections.Generic;
using Conduit.Messages;

namespace App.Commands
{
    public record BatchCommand(IReadOnlyList<Items.ForeignItem> Items) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source, itemReference);

        // Assert
        messages.Should().ContainSingle()
            .Which.Should().Contain("'Items.ForeignItem'").And.Contain("Payload is not a JsonElement");
    }

    [TestMethod]
    public void ReportACarriedTypeOfAReferencedMessage()
    {
        // Arrange -- the message and its item both live in the referenced library
        var contractsReference = CompileReference("Contracts", @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace Contracts
{
    public record ForeignItem(string Type, [AuditAs(nameof(Type))] string Payload);

    public record ForeignBatch(IReadOnlyList<ForeignItem> Items) : ICommand;
}
");

        // Act
        var messages = AuditAsMessages("namespace App { public sealed class Host { } }", contractsReference);

        // Assert
        messages.Should().ContainSingle().Which.Should().Contain("'Contracts.ForeignItem'");
    }

    [TestMethod]
    public void NotReportAWellFormedCarriedTypeFromAReferencedAssembly()
    {
        // Arrange
        var itemReference = CompileReference("Items", @"
using System.Text.Json;
using Conduit.Attributes;

namespace Items
{
    public record ForeignItem(string Type, [AuditAs(nameof(Type))] JsonElement Payload);
}
", [.. BaseReferences, JsonReference]);
        var source = @"
using System.Collections.Generic;
using Conduit.Messages;

namespace App.Commands
{
    public record BatchCommand(IReadOnlyList<Items.ForeignItem> Items) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source, itemReference);

        // Assert
        messages.Should().BeEmpty();
    }

    [TestMethod]
    public void NotWalkAnAssemblyThatDoesNotReferenceConduitContracts()
    {
        // Arrange -- a look-alike attribute of the same full name, in an assembly that cannot carry Conduit's [AuditAs]
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var lookalikeReference = CompileReference("Lookalike", @"
namespace Conduit.Attributes
{
    [System.AttributeUsage(System.AttributeTargets.Property | System.AttributeTargets.Parameter)]
    public sealed class AuditAsAttribute : System.Attribute
    {
        public AuditAsAttribute(string typeMember) { }
    }
}

namespace Lookalike
{
    public record Item(string Type, [Conduit.Attributes.AuditAs(nameof(Type))] string Payload);

    public record Holder(Item Inner);
}
", [
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDirectory, "System.Runtime.dll")),
        ]);
        var source = @"
using Conduit.Messages;

namespace App.Commands
{
    public record HoldCommand(Lookalike.Holder Holder) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source, lookalikeReference);

        // Assert
        messages.Should().BeEmpty();
    }

    [TestMethod]
    public async Task TerminateOnARecursivelyExpandingGeneric()
    {
        // Arrange -- each step constructs a new type (Tree<int>, Tree<Tree<int>>, ...); the walk keys on the definition
        var source = @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record Tree<T>(T Value, string Name, [AuditAs(nameof(Name))] string Payload, IReadOnlyList<Tree<Tree<T>>> Kids);

    public record Plant(Tree<int> Root) : ICommand;
}
";

        // Act
        var run = Task.Run(() => AuditAsMessages(source));
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30)));

        // Assert
        finished.Should().BeSameAs(run, "the walk must terminate on an expanding generic");
        (await run).Should().ContainSingle().Which.Should().Contain("'App.Commands.Tree<T>'");
    }

    [TestMethod]
    public void ReportAGenericCarriedTypeOncePerDefinition()
    {
        // Arrange -- two constructions of one misapplied definition
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record Wrapper<T>(string Type, [AuditAs(nameof(Type))] string Payload, T Value);

    public record Wrap(Wrapper<int> Numbers, Wrapper<string> Words) : ICommand;
}
";

        // Act
        var messages = AuditAsMessages(source);

        // Assert
        messages.Should().ContainSingle().Which.Should().Contain("'App.Commands.Wrapper<T>'").And.Contain("Payload is not a JsonElement");
    }

    [TestMethod]
    public void ReportSameNamedCarriedTypesFromTwoAssembliesEach()
    {
        // Arrange -- two libraries each declare Shared.Item, each carried by its own message
        static MetadataReference Library(string name) => CompileReference(name, @"
using System.Collections.Generic;
using Conduit.Attributes;
using Conduit.Messages;

namespace Shared
{
    public record Item(string Type, [AuditAs(nameof(Type))] string Payload);
}

namespace " + name + @"
{
    public record Batch(IReadOnlyList<Shared.Item> Items) : ICommand;
}
");

        // Act
        var messages = AuditAsMessages("namespace App { public sealed class Host { } }", Library("First"), Library("Second"));

        // Assert
        messages.Should().HaveCount(2).And.AllSatisfy(message => message.Should().Contain("'Shared.Item'"));
    }
}
