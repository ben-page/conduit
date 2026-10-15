using System.Globalization;
using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class AuditMarkDiagnosticShould : GeneratorTestBase
{
    [TestMethod]
    public void ReportBothMarksOnARecordParameter()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace App.Commands
{
    public record UpdateSsn(int Id, [DoNotAudit][AuditRedacted] string Ssn) : ICommand;

    public class UpdateSsnHandler : ICommandHandler<UpdateSsn>
    {
        public Task HandleAsync(UpdateSsn command, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
";
        var (_, diagnostics) = RunGenerator(source);

        diagnostics.Should().Contain(d => d.Id == "CONDUIT005" && d.GetMessage(CultureInfo.InvariantCulture).Contains("Ssn"));
    }

    [TestMethod]
    public void ReportAParameterMarkedOneWayWhosePropertyIsMarkedTheOther()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Queries
{
    public sealed class FindBySsn : IQuery<int>
    {
        public FindBySsn([AuditRedacted] string ssn) { this.Ssn = ssn; }

        [DoNotAudit]
        public string Ssn { get; }
    }
}
";
        var (_, diagnostics) = RunGenerator(source);

        diagnostics.Should().Contain(d => d.Id == "CONDUIT005");
    }

    [TestMethod]
    public void NotReportASingleMark()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Queries
{
    public record FindBySsn([AuditRedacted] string Ssn, [DoNotAudit] string Pin) : IQuery<int>;
}
";
        var (_, diagnostics) = RunGenerator(source);

        diagnostics.Should().NotContain(d => d.Id == "CONDUIT005");
    }
}
