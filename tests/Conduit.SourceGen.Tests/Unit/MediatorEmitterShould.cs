using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class MediatorEmitterShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitMediatorWithStronglyTypedOverloads()
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

        // Wire dispatch (private methods called by HandleWire*)
        mediatorSource.Should().Contain("private async Task<object?> DispatchRequestAsync(");
        mediatorSource.Should().Contain("\"App.Commands.CreateOrder\"");
        mediatorSource.Should().Contain("\"App.Commands.DeleteOrder\"");

        // SafeExecute via ConduitRuntime
        mediatorSource.Should().Contain("ConduitRuntime.SafeExecute");

        // Routing infrastructure via ConduitRuntime
        mediatorSource.Should().Contain("ConduitOptions conduitOptions");
        mediatorSource.Should().Contain("ConduitRuntime.ResolveRoute(conduitOptions,");
        mediatorSource.Should().Contain("endpoint.Transport.SendRequestAsync");

        // Wire event dispatch (private, called by HandleWireEventAsync)
        mediatorSource.Should().Contain("private async Task DispatchEventAsync(");
        mediatorSource.Should().Contain("\"App.Events.OrderCreated\"");

        // Public wire handler methods (IWireMediator)
        mediatorSource.Should().Contain("HandleWireRequestAsync(");
        mediatorSource.Should().Contain("HandleWireEventAsync(");
        mediatorSource.Should().Contain("IWireMediator");
    }
}
