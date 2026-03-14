using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class DiagnosticShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitErrorForCommandWithNoHandler()
    {
        var source = @"
using Conduit.Messages;
using System;

namespace App.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;
}
";
        var (_, diagnostics) = RunGenerator(source);

        diagnostics.Should().Contain(d => d.Id == "CONDUIT001");
    }
}
