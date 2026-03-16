using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class DispatchEmitterShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitExecuteForCommandWithResult()
    {
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

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
        dispatchSource.Should().Contain("ICommandHandler<App.Commands.CreateOrder, System.Guid>");
    }

    [TestMethod]
    public void EmitExecuteForVoidCommand()
    {
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

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
    public void EmitFetchForQuery()
    {
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

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
