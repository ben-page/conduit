using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.SourceGen.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

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
        var (output, _) = RunGenerator(source);

        var registrationSource = FindGeneratedSource(output, "ConduitRegistration");
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
        var (output, diagnostics) = RunGenerator(source);

        diagnostics.Should().NotContain(d => d.Id == "CONDUIT001");

        var registrationSource = FindGeneratedSource(output, "ConduitRegistration");
        registrationSource.Should().NotBeNull();
        // C# syntax positions use the dotted form (the '+' form would not compile).
        registrationSource.Should().Contain("typeof(App.Outer.CreateThing)");
        registrationSource.Should().Contain("Conduit.Handlers.ICommandHandler<App.Outer.CreateThing>");
        registrationSource.Should().NotContain("App.Outer+CreateThing");

        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");
        mediatorSource.Should().NotBeNull();
        // The wire discriminator string literal uses the metadata ('+') form.
        mediatorSource.Should().Contain("\"App.Outer+CreateThing\"");
        // The deserialize generic argument uses the C# ('.') form.
        mediatorSource.Should().Contain("DeserializeWire<App.Outer.CreateThing>");
    }

    [TestMethod]
    public void EmitEveryDiscoveredEventInTheRegistry()
    {
        // Arrange -- an event with a listener, one with none at compile time (a Jobs trigger or a HandleEvent override
        // may add one at runtime), a [ClientVisible] event and one deriving from it ([ClientVisible] is inherited), and a
        // [LocalOnly] event
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Attributes;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App.Events
            {
                public sealed record OrderPlaced(int OrderId) : IEvent;

                public sealed class OrderPlacedListener : IEventListener<OrderPlaced>
                {
                    public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed record OrderArchived(int OrderId) : IEvent;

                [ClientVisible]
                public record OrderShipped(int OrderId) : IEvent;

                public sealed record OrderShippedLate(int OrderId, int DaysLate) : OrderShipped(OrderId);

                [LocalOnly]
                public sealed record CacheCleared(string Key) : IEvent;
            }
            """;
        var (output, _) = RunGenerator(source);
        var generated = LoadOutput(output);
        var services = new ServiceCollection();

        // Act
        generated.GetType("Conduit.Generated.ConduitRegistration", throwOnError: true)!
            .GetMethod("AddConduit", [typeof(IServiceCollection), typeof(Action<ConduitBuilder>)])!
            .Invoke(null, [services, null]);
        var registry = (ConduitTypeRegistry)services.Single(descriptor => descriptor.ServiceType == typeof(ConduitTypeRegistry))
            .ImplementationInstance!;

        // Assert -- the registry knows every event; only the listened one is subscribed until something activates another
        registry.EventTypeNames.Where(name => name.StartsWith("App.", StringComparison.Ordinal)).Should().BeEquivalentTo(
            "App.Events.OrderPlaced", "App.Events.OrderArchived", "App.Events.OrderShipped", "App.Events.OrderShippedLate",
            "App.Events.CacheCleared");
        registry.EventTypes.Select(type => type.FullName).Should().BeEquivalentTo(registry.EventTypeNames);
        registry.ActiveEventTypeNames.Should().Equal("App.Events.OrderPlaced");
        registry.ClientVisibleEventTypeNames.Where(name => name.StartsWith("App.", StringComparison.Ordinal)).Should()
            .BeEquivalentTo("App.Events.OrderShipped", "App.Events.OrderShippedLate");
    }
}
