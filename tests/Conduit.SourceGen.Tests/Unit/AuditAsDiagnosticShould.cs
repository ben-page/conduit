using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class AuditAsDiagnosticShould : GeneratorTestBase
{
    private static readonly MetadataReference JsonReference = MetadataReference.CreateFromFile(typeof(JsonElement).Assembly.Location);

    private static List<Diagnostic> AuditAsDiagnostics(string source)
    {
        var (_, diagnostics) = RunGenerator(source, JsonReference);
        return diagnostics.Where(d => d.Id == "CONDUIT006").ToList();
    }

    [TestMethod]
    public void ReportAMemberThatIsNotAJsonElement()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record Envelope(string Type, [AuditAs(nameof(Type))] string Payload) : ICommand;
}
";

        // Act
        var diagnostics = AuditAsDiagnostics(source);

        // Assert
        diagnostics.Should().ContainSingle()
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("Payload is not a JsonElement");
    }

    [TestMethod]
    public void ReportASiblingThatDoesNotExist()
    {
        // Arrange
        var source = @"
using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record Envelope(string Type, [AuditAs(""Kind"")] JsonElement Payload) : ICommand;
}
";

        // Act
        var diagnostics = AuditAsDiagnostics(source);

        // Assert
        diagnostics.Should().ContainSingle()
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("'Kind', which is not a property");
    }

    [TestMethod]
    public void ReportASiblingThatIsNotAString()
    {
        // Arrange
        var source = @"
using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Queries
{
    public sealed class Envelope : IQuery<int>
    {
        public int Kind { get; init; }

        [AuditAs(nameof(Kind))]
        public JsonElement? Payload { get; init; }
    }
}
";

        // Act
        var diagnostics = AuditAsDiagnostics(source);

        // Assert
        diagnostics.Should().ContainSingle()
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("'Kind', which is not a string");
    }

    [TestMethod]
    public void ReportAMisuseOnABaseRecord()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Events
{
    public record BaseEnvelope(string Type, [AuditAs(nameof(Type))] object Payload);

    public record Envelope(string Type, object Payload) : BaseEnvelope(Type, Payload), IEvent;
}
";

        // Act
        var diagnostics = AuditAsDiagnostics(source);

        // Assert
        diagnostics.Should().ContainSingle()
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("App.Events.Envelope");
    }

    [TestMethod]
    public void NotReportAJsonElementNamingAStringSibling()
    {
        // Arrange
        var source = @"
using System.Text.Json;
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Commands
{
    public record Envelope(string Type, [AuditAs(nameof(Type))] JsonElement Payload) : ICommand;

    public sealed class PropertyEnvelope : ICommand
    {
        public string? Kind { get; init; }

        [AuditAs(nameof(Kind))]
        public JsonElement? Body { get; init; }
    }
}
";

        // Act
        var diagnostics = AuditAsDiagnostics(source);

        // Assert
        diagnostics.Should().BeEmpty();
    }
}
