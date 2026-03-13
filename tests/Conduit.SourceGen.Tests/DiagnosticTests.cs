using AwesomeAssertions;

namespace Conduit.SourceGen.Tests;

[TestClass]
public class DiagnosticTests : GeneratorTestBase
{
    [TestMethod]
    public void EmitsErrorForCommandWithNoHandler()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;
using System;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;
}
";
        var (_, diagnostics) = RunGenerator(source);

        diagnostics.Should().Contain(d => d.Id == "CONDUIT001");
    }
}
