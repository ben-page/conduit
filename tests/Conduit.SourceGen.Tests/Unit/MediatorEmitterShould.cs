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

        // PublishAsync resolves listeners by interface
        mediatorSource.Should().Contain("PublishAsync");
        mediatorSource.Should().Contain("GetServices<Conduit.Handlers.IEventListener<App.Events.OrderCreated>>");

        // Wire dispatch (private methods called by HandleWire*)
        mediatorSource.Should().Contain("private async Task<object?> DispatchRequestAsync(");
        mediatorSource.Should().Contain("\"App.Commands.CreateOrder\"");
        mediatorSource.Should().Contain("\"App.Commands.DeleteOrder\"");

        // Listener error collection and EventListenerException
        mediatorSource.Should().Contain("var exceptions = new List<Exception>();");
        mediatorSource.Should().Contain("exceptions.Add(ex);");
        mediatorSource.Should().Contain("EventListenerException");

        // PublishAsync catches EventListenerException (fire-and-forget)
        mediatorSource.Should().Contain("catch (Conduit.Exceptions.EventListenerException ex)");

        // Routing infrastructure via ConduitRuntime
        mediatorSource.Should().Contain("ConduitOptions conduitOptions");
        mediatorSource.Should().Contain("ConduitRuntime.ResolveRoute(conduitOptions,");
        mediatorSource.Should().Contain("ConduitRuntime.ResolveTransport(serviceProvider, endpoint)");

        // Wire event dispatch (private, called by HandleWireEventAsync)
        mediatorSource.Should().Contain("private async Task DispatchEventAsync(");
        mediatorSource.Should().Contain("\"App.Events.OrderCreated\"");

        // Public wire handler methods (IWireMediator)
        mediatorSource.Should().Contain("HandleWireRequestAsync(");
        mediatorSource.Should().Contain("HandleWireEventAsync(");
        mediatorSource.Should().Contain("IWireMediator");
    }

    [TestMethod]
    public void EmitLocalEnvelopeDispatcherMembers()
    {
        // Arrange — one capturable F&F event, one Immediate, one LocalOnly plain event
        const string source = """
            using Conduit.Attributes;
            using Conduit.Messages;
            using Conduit.Handlers;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestApp;

            [FireAndForget]
            public record CapturedEvent(string Detail) : IEvent;

            [FireAndForget(Immediate = true)]
            public record ImmediateEvent(string Detail) : IEvent;

            [LocalOnly]
            public record LocalOnlyEvent(string Detail) : IEvent;

            public static class Outer
            {
                [FireAndForget]
                public record NestedCapturedEvent(string Detail) : IEvent;
            }

            public class CapturedListener : IEventListener<CapturedEvent>
            {
                public Task HandleAsync(CapturedEvent e, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        // Act
        var (trees, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(trees, "ConduitMediator");

        // Assert — interface on the class, classification switches keyed by METADATA names, publish entry
        mediatorSource.Should().NotBeNull();
        mediatorSource.Should().Contain("ILocalEnvelopeDispatcher");
        mediatorSource.Should().Contain("public bool DeliversLocally(string messageType)");
        mediatorSource.Should().Contain("\"TestApp.CapturedEvent\" => true");         // capturable F&F
        mediatorSource.Should().NotContain("\"TestApp.ImmediateEvent\" => true");     // Immediate is not outbox-delivered
        mediatorSource.Should().Contain("public bool IsLocalOnly(string messageType)");
        mediatorSource.Should().Contain("\"TestApp.LocalOnlyEvent\" => true");
        mediatorSource.Should().Contain("public async Task PublishLocalEnvelopeAsync(");

        // Nested types are the one case where metadata ('+') and C# ('.') forms differ: switch STRING
        // keys must be the metadata form (what DispatchEnvelope.Type carries at runtime), while the
        // Deserialize generic argument must be the C# form. Non-nested fixtures cannot catch a mixup.
        mediatorSource.Should().Contain("\"TestApp.Outer+NestedCapturedEvent\" => true");
        mediatorSource.Should().NotContain("\"TestApp.Outer.NestedCapturedEvent\" =>");
        mediatorSource.Should().Contain("\"TestApp.Outer+NestedCapturedEvent\" => JsonSerializer.Deserialize<TestApp.Outer.NestedCapturedEvent>");
        mediatorSource.Should().Contain("\"TestApp.ImmediateEvent\" => JsonSerializer.Deserialize<TestApp.ImmediateEvent>");

        // Assert — registration delegates the new interface to the same mediator instance
        var registrationSource = FindGeneratedSource(trees, "ConduitRegistration");
        registrationSource.Should().NotBeNull();
        registrationSource.Should().Contain(
            "services.AddTransient<Conduit.ILocalEnvelopeDispatcher>(sp => sp.GetRequiredService<ConduitMediator>());");
    }
}
