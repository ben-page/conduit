using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class DiagnosticShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitWarningForCommandWithNoHandler()
    {
        // Arrange
        var source = @"
using Conduit.Messages;
using System;

namespace App.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Should().Contain(d => d.Id == "CONDUIT001");
    }

    [TestMethod]
    public void SuppressWarningForRemoteContractNamespace()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;
using System;

[assembly: RemoteContracts(""Remote.Commands"")]

namespace Remote.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Should().NotContain(d => d.Id == "CONDUIT001");
    }

    [TestMethod]
    public void StillWarnForLocalTypesWhenRemoteContractsDeclared()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;
using System;

[assembly: RemoteContracts(""Remote.Commands"")]

namespace Remote.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;
}

namespace Local.Commands
{
    public record DeleteOrder(Guid Id) : ICommand;
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert — Remote.Commands.CreateOrder suppressed, Local.Commands.DeleteOrder warned
        diagnostics.Should().NotContain(d =>
            d.Id == "CONDUIT001" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Remote.Commands.CreateOrder"));
        diagnostics.Should().Contain(d =>
            d.Id == "CONDUIT001" && d.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains("Local.Commands.DeleteOrder"));
    }
}
