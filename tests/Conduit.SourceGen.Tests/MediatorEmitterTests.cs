using AwesomeAssertions;

namespace Conduit.SourceGen.Tests;

[TestClass]
public class MediatorEmitterTests : GeneratorTestBase
{
    [TestMethod]
    public void EmitsMediatorWithStronglyTypedOverloads()
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

    public record DeleteOrder(Guid Id) : ICommand;

    public class DeleteOrderHandler : ICommandHandler<DeleteOrder>
    {
        public Task HandleAsync(DeleteOrder command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

namespace App.Queries
{
    public record GetOrder(Guid Id) : IQuery<string>;

    public class GetOrderHandler : IQueryHandler<GetOrder, string>
    {
        public Task<string> HandleAsync(GetOrder query, CancellationToken cancellationToken)
            => Task.FromResult(""order"");
    }
}

namespace App.Events
{
    public record OrderCreated(Guid Id) : IEvent;

    public class OrderCreatedListener : IEventListener<OrderCreated>
    {
        public Task HandleAsync(OrderCreated @event, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";
        var (trees, _) = RunGenerator(source);

        var mediatorSource = FindGeneratedSource(trees, "ConduitMediator");
        mediatorSource.Should().NotBeNull();

        // Strongly-typed overloads
        mediatorSource.Should().Contain("App.Commands.CreateOrder command");
        mediatorSource.Should().Contain("App.Commands.DeleteOrder command");
        mediatorSource.Should().Contain("App.Queries.GetOrder query");

        // Interface fallbacks
        mediatorSource.Should().Contain("IMediator.ExecuteAsync<TResult>");
        mediatorSource.Should().Contain("IMediator.ExecuteAsync(");
        mediatorSource.Should().Contain("IMediator.FetchAsync<TResult>");

        // PublishAsync with listener
        mediatorSource.Should().Contain("PublishAsync");
        mediatorSource.Should().Contain("OrderCreatedListener");

        // Wire dispatch
        mediatorSource.Should().Contain("DispatchAsync(");
        mediatorSource.Should().Contain("\"CreateOrder\"");
        mediatorSource.Should().Contain("\"DeleteOrder\"");

        // SafeExecute
        mediatorSource.Should().Contain("SafeExecute");
    }
}
