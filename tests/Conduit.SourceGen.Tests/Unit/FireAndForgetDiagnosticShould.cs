using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class FireAndForgetDiagnosticShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitWarningForFireAndForgetOnCommandWithResult()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;
using System;

namespace App.Commands
{
    [FireAndForget]
    public record CreateOrder(string CustomerId) : ICommand<Guid>;
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Should().Contain(d => d.Id == "CONDUIT002");
    }

    [TestMethod]
    public void EmitWarningForFireAndForgetOnQuery()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Messages;

namespace App.Queries
{
    [FireAndForget]
    public record GetOrders(string CustomerId) : IQuery<string>;
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Should().Contain(d => d.Id == "CONDUIT002");
    }

    [TestMethod]
    public void NotEmitWarningForFireAndForgetOnVoidCommand()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace App.Commands
{
    [FireAndForget]
    public record ImportData(string Path) : ICommand;

    public class ImportDataHandler : ICommandHandler<ImportData>
    {
        public Task HandleAsync(ImportData command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Should().NotContain(d => d.Id == "CONDUIT002");
    }

    [TestMethod]
    public void NotEmitWarningForFireAndForgetOnEvent()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace App.Events
{
    [FireAndForget]
    public record AuditLog(string Action) : IEvent;

    public class AuditLogListener : IEventListener<AuditLog>
    {
        public Task HandleAsync(AuditLog @event, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";

        // Act
        var (_, diagnostics) = RunGenerator(source);

        // Assert
        diagnostics.Should().NotContain(d => d.Id == "CONDUIT002");
    }
}
