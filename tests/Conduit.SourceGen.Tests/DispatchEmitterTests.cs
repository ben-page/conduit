using AwesomeAssertions;

namespace Conduit.SourceGen.Tests;

[TestClass]
public class DispatchEmitterTests : GeneratorTestBase
{
    [TestMethod]
    public void EmitsExecuteForCommandWithResult()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands
{
    public record CreateOrder(string CustomerId) : ICommand<Guid>;

    public class CreateOrderHandler : ICommandHandler<CreateOrder, Guid>
    {
        public Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken)
            => Task.FromResult(Guid.NewGuid());
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();
        dispatchSource.Should().Contain("Execute(");
        dispatchSource.Should().Contain("App.Commands.CreateOrder command");
        dispatchSource.Should().Contain("((Conduit.Messages.ICommand<System.Guid>)command).Validate()");
        dispatchSource.Should().Contain("CreateOrderHandler");
    }

    [TestMethod]
    public void EmitsExecuteForVoidCommand()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

[assembly: DispatchableNamespace(""App"")]

namespace App.Commands
{
    public record DeleteOrder(Guid Id) : ICommand;

    public class DeleteOrderHandler : ICommandHandler<DeleteOrder>
    {
        public Task HandleAsync(DeleteOrder command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();
        dispatchSource.Should().Contain("async Task Execute(");
        dispatchSource.Should().Contain("ExecuteVoid(");
    }

    [TestMethod]
    public void EmitsFetchForQuery()
    {
        var source = @"
using Conduit.Attributes;
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

[assembly: DispatchableNamespace(""App"")]

namespace App.Queries
{
    public record GetOrder(Guid Id) : IQuery<string>;

    public class GetOrderHandler : IQueryHandler<GetOrder, string>
    {
        public Task<string> HandleAsync(GetOrder query, CancellationToken cancellationToken)
            => Task.FromResult(""order"");
    }
}
";
        var (trees, _) = RunGenerator(source);

        var dispatchSource = FindGeneratedSource(trees, "ConduitDispatch");
        dispatchSource.Should().NotBeNull();
        dispatchSource.Should().Contain("Fetch(");
        dispatchSource.Should().Contain("App.Queries.GetOrder query");
    }
}
