using System.Globalization;
using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;

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

    [TestMethod]
    public void PointAtTheDeclaringType()
    {
        // Arrange -- one command declared here, one in a referenced assembly, neither with a handler
        var contracts = CompileReference("Contracts", """
            using Conduit.Messages;

            namespace Contracts.Commands;

            public sealed record ShipOrder(int OrderId) : ICommand;
            """);
        const string source = """
            using Conduit.Messages;

            namespace App.Commands
            {
                public sealed record CreateOrder(string CustomerId) : ICommand<System.Guid>;
            }
            """;

        // Act
        var (_, diagnostics) = RunGenerator(source, contracts);

        // Assert -- the local one is located at its declaration's name, in the compilation's own tree (so a #pragma and
        // the IDE apply); the referenced one has no source to point at
        var local = diagnostics.Single(d => d.Id == "CONDUIT001" && Message(d).Contains("App.Commands.CreateOrder", StringComparison.Ordinal));
        local.Location.IsInSource.Should().BeTrue();
        local.Location.SourceTree!.GetText().ToString(local.Location.SourceSpan).Should().Be("CreateOrder");
        local.Location.GetLineSpan().StartLinePosition.Line.Should().Be(4);
        var referenced = diagnostics.Single(d => d.Id == "CONDUIT001" && Message(d).Contains("Contracts.Commands.ShipOrder", StringComparison.Ordinal));
        referenced.Location.Should().Be(Location.None);
    }

    [TestMethod]
    public void SuppressAWarningWithAPragmaAroundTheType()
    {
        // Arrange
        const string source = """
            using Conduit.Messages;

            namespace App.Commands
            {
            #pragma warning disable CONDUIT001 // routed to another service
                public sealed record ShipOrder(int OrderId) : ICommand;
            #pragma warning restore CONDUIT001

                public sealed record CancelOrder(int OrderId) : ICommand;
            }
            """;

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Single(d => d.Id == "CONDUIT001" && Message(d).Contains("App.Commands.ShipOrder", StringComparison.Ordinal)).IsSuppressed.Should().BeTrue();
        diagnostics.Single(d => d.Id == "CONDUIT001" && Message(d).Contains("App.Commands.CancelOrder", StringComparison.Ordinal)).IsSuppressed.Should().BeFalse();
    }

    [TestMethod]
    public void PointAMultipleHandlerErrorAtTheMessageAndEachHandler()
    {
        // Arrange
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App.Commands
            {
                public sealed record ArchiveOrder(int OrderId) : ICommand;

                public sealed class ArchiveOrderHandler : ICommandHandler<ArchiveOrder>
                {
                    public Task HandleAsync(ArchiveOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed class LegacyArchiveOrderHandler : ICommandHandler<ArchiveOrder>
                {
                    public Task HandleAsync(ArchiveOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        var error = diagnostics.Single(d => d.Id == "CONDUIT002");
        Located(error.Location).Should().Be("ArchiveOrder");
        error.AdditionalLocations.Select(Located).Should().Equal("ArchiveOrderHandler", "LegacyArchiveOrderHandler");
    }

    private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    // The source text a location covers.
    private static string Located(Location location) => location.SourceTree!.GetText().ToString(location.SourceSpan);
}
