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
        registrationSource.Should().Contain("AddTransient<Conduit.Handlers.ICommandHandler<App.Commands.CreateOrder, System.Guid>, App.Commands.CreateOrderHandler>()");
        registrationSource.Should().Contain("AddTransient<Conduit.Handlers.IEventListener<App.Events.OrderCreated>, App.Events.OrderCreatedListener>()");
    }

    [TestMethod]
    public void EmitNestedTypeAsCSharpTypeRefInRegistrationButMetadataNameOnWire()
    {
        // A nested message type: registry typeof()/handler interfaces must use the C# ('.') form;
        // the wire discriminator must use the metadata ('+') form (= runtime Type.FullName).
        var source = @"
using Conduit.Handlers;
using Conduit.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace App
{
    public class Outer
    {
        public record CreateThing(string Data) : ICommand;
    }

    public class CreateThingHandler : ICommandHandler<Outer.CreateThing>
    {
        public Task HandleAsync(Outer.CreateThing command, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
";
        var (trees, diagnostics) = RunGenerator(source);

        diagnostics.Should().NotContain(d => d.Id == "CONDUIT001");

        var registrationSource = FindGeneratedSource(trees, "ConduitRegistration");
        registrationSource.Should().NotBeNull();
        // C# syntax positions use the dotted form (the '+' form would not compile).
        registrationSource.Should().Contain("typeof(App.Outer.CreateThing)");
        registrationSource.Should().Contain("Conduit.Handlers.ICommandHandler<App.Outer.CreateThing>");
        registrationSource.Should().NotContain("App.Outer+CreateThing");

        var mediatorSource = FindGeneratedSource(trees, "ConduitMediator");
        mediatorSource.Should().NotBeNull();
        // The wire discriminator string literal uses the metadata ('+') form.
        mediatorSource.Should().Contain("\"App.Outer+CreateThing\"");
        // The deserialize generic argument uses the C# ('.') form.
        mediatorSource.Should().Contain("Deserialize<App.Outer.CreateThing>");
    }
}
