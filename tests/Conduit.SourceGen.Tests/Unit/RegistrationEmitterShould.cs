using AwesomeAssertions;
using Conduit.SourceGen.Tests.Support;

namespace Conduit.SourceGen.Tests.Unit;

[TestClass]
public class RegistrationEmitterShould : GeneratorTestBase
{
    [TestMethod]
    public void EmitHandlerRegistration()
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

        var registrationSource = FindGeneratedSource(trees, "ConduitRegistration");
        registrationSource.Should().NotBeNull();
        registrationSource.Should().Contain("AddConduit(");
        registrationSource.Should().Contain("AddConduitCore");
        registrationSource.Should().Contain("AddTransient<App.Commands.CreateOrderHandler>()");
        registrationSource.Should().Contain("AddTransient<App.Events.OrderCreatedListener>()");
    }
}
