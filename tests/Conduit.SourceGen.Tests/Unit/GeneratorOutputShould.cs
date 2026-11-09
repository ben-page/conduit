using System.Globalization;
using System.Reflection;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Messages;
using Conduit.Runtime;
using Conduit.SourceGen.Tests.Support;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.SourceGen.Tests.Unit;

/// <summary>
/// The code the generator emits compiles in the consuming project, for every message shape it accepts -- every emitted
/// file, the REST endpoints and resilience dispatch included (both feature assemblies are referenced) -- and a shape it
/// cannot emit is reported instead of emitted.
/// </summary>
[TestClass]
public class GeneratorOutputShould : GeneratorTestBase
{
    /// <summary>
    /// Every message shape the generator tests exercise, in one compilation: each kind with and without a handler, a
    /// nested type, every event attribute, a type-level [DoNotAudit], a reference-type, nullable and value-type result.
    /// </summary>
    private const string SupportTypesSource = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Conduit.Attributes;
        using Conduit.Handlers;
        using Conduit.Messages;

        namespace App.Models
        {
            public sealed record OrderDto(Guid Id, string CustomerId);
        }

        namespace App.Commands
        {
            public sealed record CreateOrder(string CustomerId) : ICommand<Guid>;

            public sealed class CreateOrderHandler : ICommandHandler<CreateOrder, Guid>
            {
                public Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
            }

            public sealed record DeleteOrder(Guid Id) : ICommand;

            public sealed class DeleteOrderHandler : ICommandHandler<DeleteOrder>
            {
                public Task HandleAsync(DeleteOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [FireAndForget]
            public sealed record RecalculateTotals(Guid OrderId) : ICommand;

            public sealed class RecalculateTotalsHandler : ICommandHandler<RecalculateTotals>
            {
                public Task HandleAsync(RecalculateTotals command, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [DoNotAudit]
            public sealed record StoreSecret(string Secret) : ICommand<int>;

            public sealed class StoreSecretHandler : ICommandHandler<StoreSecret, int>
            {
                public Task<int> HandleAsync(StoreSecret command, CancellationToken cancellationToken) => Task.FromResult(1);
            }

            // Routed elsewhere: no local handler (CONDUIT001), so its overloads are route-only.
            public sealed record ShipOrder(Guid Id) : ICommand<bool>;

            public sealed record ArchiveOrder(Guid Id) : ICommand;

            public static class Billing
            {
                public sealed record ChargeCard(string Token) : ICommand<string>;

                public sealed class ChargeCardHandler : ICommandHandler<ChargeCard, string>
                {
                    public Task<string> HandleAsync(ChargeCard command, CancellationToken cancellationToken) => Task.FromResult("charged");
                }
            }
        }

        namespace App.Queries
        {
            public sealed record GetOrder(Guid Id) : IQuery<App.Models.OrderDto?>;

            public sealed class GetOrderHandler : IQueryHandler<GetOrder, App.Models.OrderDto?>
            {
                public Task<App.Models.OrderDto?> HandleAsync(GetOrder query, CancellationToken cancellationToken)
                    => Task.FromResult<App.Models.OrderDto?>(null);
            }

            public sealed record ListOrders(int Page) : IQuery<IReadOnlyList<App.Models.OrderDto>>;

            public sealed class ListOrdersHandler : IQueryHandler<ListOrders, IReadOnlyList<App.Models.OrderDto>>
            {
                public Task<IReadOnlyList<App.Models.OrderDto>> HandleAsync(ListOrders query, CancellationToken cancellationToken)
                    => Task.FromResult<IReadOnlyList<App.Models.OrderDto>>([]);
            }

            public sealed record CountOrders : IQuery<int>;
        }

        namespace App.Events
        {
            public sealed record OrderCreated(Guid Id) : IEvent;

            public sealed class OrderCreatedListener : IEventListener<OrderCreated>
            {
                public Task HandleAsync(OrderCreated @event, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public sealed class OrderCreatedAuditor : IEventListener<OrderCreated>
            {
                public Task HandleAsync(OrderCreated @event, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [LocalOnly]
            public sealed record CacheInvalidated(string Key) : IEvent;

            [FireAndForget]
            public sealed record OrderShipped(Guid Id) : IEvent;

            public sealed class OrderShippedListener : IEventListener<OrderShipped>
            {
                public Task HandleAsync(OrderShipped @event, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            [FireAndForget(Immediate = true)]
            public sealed record OrderViewed(Guid Id) : IEvent;

            // No listener at compile time: one may be registered at runtime.
            public sealed record OrderArchived(Guid Id) : IEvent;

            public static class Outer
            {
                [FireAndForget]
                public sealed record NestedEvent(string Detail) : IEvent;
            }
        }
        """;

    [TestMethod]
    public void CompileForTheSupportTypes()
    {
        // Act
        var (output, _) = RunEveryEmitter(SupportTypesSource);

        // Assert -- RunGenerator asserted the compilation; every file the generator can emit is in it
        foreach (var hintName in new[]
        {
            "ConduitDispatch", "ConduitRegistration", "ConduitMediator", "ConduitRestEndpoints", "ConduitResilienceDispatch",
        })
        {
            FindGeneratedSource(output, hintName).Should().NotBeNull(hintName + " is emitted and compiled");
        }
    }

    [TestMethod]
    public void ReportGenericMessageTypes()
    {
        // Arrange -- a message is dispatched by its full type name, which a generic type does not have
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App.Generic
            {
                public sealed record Wrap<T>(T Value) : ICommand;

                public sealed class WrapHandler<T> : ICommandHandler<Wrap<T>>
                {
                    public Task HandleAsync(Wrap<T> command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed record Lookup<TKey>(TKey Key) : IQuery<string>;

                public sealed record Changed<T>(T Value) : IEvent;

                public sealed class TextChangedListener : IEventListener<Changed<string>>
                {
                    public Task HandleAsync(Changed<string> @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public static class Outer<T>
                {
                    public sealed record Inner : IEvent;
                }

                // The non-generic message beside them is still emitted.
                public sealed record Ping : ICommand;

                public sealed class PingHandler : ICommandHandler<Ping>
                {
                    public Task HandleAsync(Ping command, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, diagnostics) = RunEveryEmitter(source);

        // Assert
        var generic = diagnostics.Where(diagnostic => diagnostic.Id == "CONDUIT007").ToList();
        generic.Should().AllSatisfy(diagnostic => diagnostic.Severity.Should().Be(DiagnosticSeverity.Error));
        generic.Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture))
            .Should().HaveCount(4)
            .And.ContainSingle(message => message.Contains("'App.Generic.Wrap<T>'"))
            .And.ContainSingle(message => message.Contains("'App.Generic.Lookup<TKey>'"))
            .And.ContainSingle(message => message.Contains("'App.Generic.Changed<T>'"))
            .And.ContainSingle(message => message.Contains("'App.Generic.Outer<T>.Inner'"));
        var generated = GeneratedText(output);
        generated.Should().Contain("App.Generic.Ping");
        generated.Should().NotContain("Wrap").And.NotContain("Lookup").And.NotContain("Changed").And.NotContain("Inner");
    }

    [TestMethod]
    public void SkipInternalMessagesFromReferencedAssemblies()
    {
        // Arrange -- the generating assembly cannot name an internal type of the assembly it references
        var remoteReference = CompileReference("Remote", """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace Remote
            {
                public sealed record PublicPing : ICommand;

                internal sealed record InternalPing : ICommand;

                internal sealed class InternalPingHandler : ICommandHandler<InternalPing>
                {
                    public Task HandleAsync(InternalPing command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                internal sealed record InternalNotice : IEvent;
            }
            """);
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;

            namespace Host
            {
                public sealed class PublicPingHandler : ICommandHandler<Remote.PublicPing>
                {
                    public Task HandleAsync(Remote.PublicPing command, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, diagnostics) = RunEveryEmitter(source, remoteReference);

        // Assert -- RunGenerator asserted no CS0122; the internal handler is still reported as unreachable
        diagnostics.Should().ContainSingle(diagnostic => diagnostic.Id == "CONDUIT004")
            .Which.GetMessage(CultureInfo.InvariantCulture).Should().Contain("Remote.InternalPing");
        diagnostics.Should().NotContain(diagnostic => diagnostic.Id == "CONDUIT001");
        var generated = GeneratedText(output);
        generated.Should().Contain("Remote.PublicPing");
        generated.Should().NotContain("InternalPing").And.NotContain("InternalNotice");
    }

    [TestMethod]
    public void SkipListenersOfEventsTheGeneratedCodeCannotName()
    {
        // Arrange -- a listener is registered under its event's type, so a listener the generated code can name is still
        // not wired when its event cannot be named: internal to a referenced assembly, private, or a type parameter
        var remoteReference = CompileReference("Remote", """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace Remote
            {
                internal sealed record InternalNotice : IEvent;

                public sealed class InternalNoticeListener : IEventListener<InternalNotice>
                {
                    Task IEventListener<InternalNotice>.HandleAsync(InternalNotice @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """);
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace Host
            {
                public sealed record Ponged : IEvent;

                public sealed class PongedListener : IEventListener<Ponged>
                {
                    public Task HandleAsync(Ponged @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public static class Outer
                {
                    private sealed record Hidden : IEvent;

                    public sealed class HiddenListener : IEventListener<Hidden>
                    {
                        Task IEventListener<Hidden>.HandleAsync(Hidden @event, CancellationToken cancellationToken) => Task.CompletedTask;
                    }
                }

                public sealed class EveryEventListener<TEvent> : IEventListener<TEvent>
                    where TEvent : IEvent
                {
                    public Task HandleAsync(TEvent @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, _) = RunEveryEmitter(source, remoteReference);

        // Assert -- RunGenerator asserted no CS0122/CS0246; only the listener of a nameable event is registered
        var registration = FindGeneratedSource(output, "ConduitRegistration");
        registration.Should().Contain("IEventListener<Host.Ponged>, Host.PongedListener>");
        var generated = GeneratedText(output);
        generated.Should().NotContain("InternalNotice").And.NotContain("Hidden").And.NotContain("EveryEventListener")
            .And.NotContain("TEvent");
    }

    [TestMethod]
    public void SkipFileLocalTypes()
    {
        // Arrange -- a file-local type is visible only in its own file, never in a generated one
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App.Local
            {
                file sealed record Secret : ICommand;

                public sealed record Visible : ICommand;

                file sealed class VisibleHandler : ICommandHandler<Visible>
                {
                    public Task HandleAsync(Visible command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed record Noticed : IEvent;

                file sealed class NoticedListener : IEventListener<Noticed>
                {
                    public Task HandleAsync(Noticed @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, diagnostics) = RunEveryEmitter(source);

        // Assert -- RunGenerator asserted the compilation; the file-local handler is reported as unreachable
        diagnostics.Should().Contain(diagnostic => diagnostic.Id == "CONDUIT004"
            && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("App.Local.Visible"));
        var generated = GeneratedText(output);
        generated.Should().Contain("App.Local.Visible").And.Contain("App.Local.Noticed");
        generated.Should().NotContain("Secret").And.NotContain("VisibleHandler").And.NotContain("NoticedListener");
    }

    [TestMethod]
    public void CompileWhenOneMessageDerivesFromAnother()
    {
        // Arrange -- each kind has a concrete message deriving from another; the derived event is declared before its
        // base, so neither discovery order is the one that happens to compile
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Attributes;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App.Orders
            {
                public record PlaceOrder(string CustomerId) : ICommand<Guid>;

                public sealed record PlaceRushOrder(string CustomerId, int Hours) : PlaceOrder(CustomerId);

                public sealed class PlaceOrderHandler : ICommandHandler<PlaceOrder, Guid>
                {
                    public Task<Guid> HandleAsync(PlaceOrder command, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
                }

                public sealed class PlaceRushOrderHandler : ICommandHandler<PlaceRushOrder, Guid>
                {
                    public Task<Guid> HandleAsync(PlaceRushOrder command, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
                }

                public record CancelOrder(Guid Id) : ICommand;

                public sealed record CancelRushOrder(Guid Id) : CancelOrder(Id);

                public sealed class CancelOrderHandler : ICommandHandler<CancelOrder>
                {
                    public Task HandleAsync(CancelOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed class CancelRushOrderHandler : ICommandHandler<CancelRushOrder>
                {
                    public Task HandleAsync(CancelRushOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public record FindOrder(Guid Id) : IQuery<string>;

                public sealed record FindRushOrder(Guid Id) : FindOrder(Id);

                public sealed class FindOrderHandler : IQueryHandler<FindOrder, string>
                {
                    public Task<string> HandleAsync(FindOrder query, CancellationToken cancellationToken) => Task.FromResult("order");
                }

                public sealed class FindRushOrderHandler : IQueryHandler<FindRushOrder, string>
                {
                    public Task<string> HandleAsync(FindRushOrder query, CancellationToken cancellationToken) => Task.FromResult("rush");
                }

                [FireAndForget]
                public sealed record ExpressOrderPlaced(Guid Id) : OrderPlaced(Id);

                [FireAndForget]
                public record OrderPlaced(Guid Id) : IEvent;

                [LocalOnly]
                public record OrderNoted(Guid Id) : IEvent;

                public sealed record UrgentOrderNoted(Guid Id) : OrderNoted(Id);

                public sealed class OrderPlacedListener : IEventListener<OrderPlaced>
                {
                    public Task HandleAsync(OrderPlaced @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, _) = RunEveryEmitter(source);

        // Assert -- RunGenerator asserted the compilation; each type is dispatched by its own full name
        var mediator = FindGeneratedSource(output, "ConduitMediator");
        mediator.Should().Contain("\"App.Orders.PlaceRushOrder\" => ConduitRuntime.CastTask<System.Guid, TResult>(ExecuteAsync((App.Orders.PlaceRushOrder)(object)command");
        mediator.Should().Contain("\"App.Orders.PlaceOrder\" => ConduitRuntime.CastTask<System.Guid, TResult>(ExecuteAsync((App.Orders.PlaceOrder)(object)command");
        mediator.Should().Contain("\"App.Orders.CancelRushOrder\" => ExecuteAsync((App.Orders.CancelRushOrder)(object)command");
        mediator.Should().Contain("\"App.Orders.FindRushOrder\" => ConduitRuntime.CastTask<string, TResult>(FetchAsync((App.Orders.FindRushOrder)(object)query");
        mediator.Should().Contain("case \"App.Orders.ExpressOrderPlaced\":");
        mediator.Should().Contain("case \"App.Orders.OrderPlaced\":");
    }

    [TestMethod]
    public void EscapeKeywordNamedTypes()
    {
        // Arrange -- a C# keyword is a legal type or namespace name when escaped; its full type name is unescaped
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App.@namespace
            {
                public sealed record @event(string Detail) : IEvent;

                public sealed class EventListener : IEventListener<@event>
                {
                    public Task HandleAsync(@event @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed record @object(int Id) : IQuery<@event>;

                public sealed class @checked : IQueryHandler<@object, @event>
                {
                    public Task<@event> HandleAsync(@object query, CancellationToken cancellationToken) => Task.FromResult(new @event("found"));
                }

                public sealed record @class : ICommand;

                public sealed class ClassHandler : ICommandHandler<@class>
                {
                    public Task HandleAsync(@class command, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, _) = RunEveryEmitter(source);

        // Assert -- RunGenerator asserted the compilation; type positions are escaped, wire names are not
        var mediator = FindGeneratedSource(output, "ConduitMediator");
        mediator.Should().Contain("App.@namespace.@event");
        mediator.Should().Contain("\"App.namespace.event\"");
        FindGeneratedSource(output, "ConduitRegistration").Should().Contain("App.@namespace.@checked");
    }

    [TestMethod]
    public void ReportDuplicateFullTypeNamesAcrossAssemblies()
    {
        // Arrange -- two referenced libraries each declare Shared.Ping and Shared.Pinged, so each name means two types
        static MetadataReference Library(string name) => CompileReference(name, """
            using Conduit.Messages;

            namespace Shared
            {
                public sealed record Ping(int Value) : ICommand;

                public sealed record Pinged(int Value) : IEvent;
            }
            """);
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace Host
            {
                public sealed record Pong : ICommand;

                public sealed class PongHandler : ICommandHandler<Pong>
                {
                    public Task HandleAsync(Pong command, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, diagnostics) = RunEveryEmitter(source, Library("First"), Library("Second"));

        // Assert -- RunGenerator asserted the generator did not throw; neither copy is emitted
        var duplicates = diagnostics.Where(diagnostic => diagnostic.Id == "CONDUIT008").ToList();
        duplicates.Should().AllSatisfy(diagnostic =>
        {
            diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
            diagnostic.GetMessage(CultureInfo.InvariantCulture).Should().Contain("First").And.Contain("Second");
        });
        duplicates.Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture))
            .Should().HaveCount(2)
            .And.ContainSingle(message => message.Contains("'Shared.Ping'"))
            .And.ContainSingle(message => message.Contains("'Shared.Pinged'"));
        var generated = GeneratedText(output);
        generated.Should().Contain("Host.Pong");
        generated.Should().NotContain("Shared.Ping");
    }

    [TestMethod]
    public async Task InheritLocalOnlyFromABaseEvent()
    {
        // Arrange -- [LocalOnly] is inherited (AttributeUsage's default), so an event deriving from a [LocalOnly]
        // event is kept in process too
        const string source = """
            using Conduit.Attributes;
            using Conduit.Messages;

            namespace App.Events
            {
                [LocalOnly]
                public record CacheCleared(string Key) : IEvent;

                public sealed record RegionCacheCleared(string Key, string Region) : CacheCleared(Key);

                public sealed record OrderShipped(int OrderId) : IEvent;
            }
            """;
        var (output, _) = RunGenerator(source);
        var generated = LoadOutput(output);
        var broadcast = new RecordingTransport();
        await using var services = BuildGeneratedServices(generated, broadcast);
        var mediator = services.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync((IEvent)NewMessage(generated, "App.Events.RegionCacheCleared", "prices", "eu"));
        await mediator.PublishAsync((IEvent)NewMessage(generated, "App.Events.CacheCleared", "prices"));
        await mediator.PublishAsync((IEvent)NewMessage(generated, "App.Events.OrderShipped", 7));

        // Assert -- only the event that is not local-only reached the transport; the outbox's classification agrees
        broadcast.Events.Should().Equal("App.Events.OrderShipped");
        var dispatcher = services.GetRequiredService<ILocalEnvelopeDispatcher>();
        dispatcher.IsLocalOnly("App.Events.RegionCacheCleared").Should().BeTrue();
        dispatcher.IsLocalOnly("App.Events.OrderShipped").Should().BeFalse();
        dispatcher.IsEvent("App.Events.RegionCacheCleared").Should().BeTrue();
        dispatcher.IsEvent("App.Events.OrderShipped").Should().BeTrue();
    }

    [TestMethod]
    public async Task InheritFireAndForgetFromABaseCommand()
    {
        // Arrange -- [FireAndForget] is inherited, so a routed command deriving from a [FireAndForget] command is sent
        // with no reply expected, as its base is
        const string source = """
            using Conduit.Attributes;
            using Conduit.Messages;

            namespace App.Commands
            {
                [FireAndForget]
                public record RecordAudit(string Detail) : ICommand;

                public sealed record RecordSecurityAudit(string Detail, string Actor) : RecordAudit(Detail);

                public sealed record ChargeCard(string Token) : ICommand;
            }
            """;
        var (output, _) = RunGenerator(source);
        var generated = LoadOutput(output);
        var remote = new RecordingTransport();
        await using var services = BuildGeneratedServices(generated, new RecordingTransport(), conduit => conduit.AddEndpoint("remote", endpoint =>
        {
            RouteType(endpoint, generated, "App.Commands.RecordAudit");
            RouteType(endpoint, generated, "App.Commands.RecordSecurityAudit");
            RouteType(endpoint, generated, "App.Commands.ChargeCard");
            endpoint.UseTransport(remote);
        }));
        var mediator = services.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync((ICommand)NewMessage(generated, "App.Commands.RecordAudit", "opened"));
        await mediator.ExecuteAsync((ICommand)NewMessage(generated, "App.Commands.RecordSecurityAudit", "opened", "admin"));
        await mediator.ExecuteAsync((ICommand)NewMessage(generated, "App.Commands.ChargeCard", "tok"));

        // Assert -- the outbox's classification agrees, so a relayed derived command is sent with no reply expected too
        remote.Events.Should().Equal("App.Commands.RecordAudit", "App.Commands.RecordSecurityAudit");
        remote.Requests.Should().Equal("App.Commands.ChargeCard");
        var dispatcher = services.GetRequiredService<ILocalEnvelopeDispatcher>();
        dispatcher.IsFireAndForget("App.Commands.RecordSecurityAudit").Should().BeTrue();
        dispatcher.IsFireAndForget("App.Commands.ChargeCard").Should().BeFalse();
        dispatcher.IsEvent("App.Commands.RecordSecurityAudit").Should().BeFalse();
    }

    [TestMethod]
    public async Task NotBroadcastAnEventTypeTheCompilationNeverDiscovered()
    {
        // Arrange -- the broadcast gate fails closed: an event type the generator did not discover (here a private one,
        // which it cannot name) is not sent to a transport, with or without a [LocalOnly] event in the compilation
        const string source = """
            using Conduit.Messages;

            namespace App.Events
            {
                public sealed record OrderShipped(int OrderId) : IEvent;

                public static class Telemetry
                {
                    private sealed record Heartbeat(int Sequence) : IEvent;

                    public static IEvent NewHeartbeat(int sequence) => new Heartbeat(sequence);
                }
            }
            """;
        var (output, _) = RunGenerator(source);
        var generated = LoadOutput(output);
        var broadcast = new RecordingTransport();
        await using var services = BuildGeneratedServices(generated, broadcast);
        var mediator = services.GetRequiredService<IMediator>();
        var heartbeat = (IEvent)generated.GetType("App.Events.Telemetry", throwOnError: true)!
            .GetMethod("NewHeartbeat")!.Invoke(null, [1])!;

        // Act
        await mediator.PublishAsync(heartbeat);
        await mediator.PublishAsync((IEvent)NewMessage(generated, "App.Events.OrderShipped", 7));

        // Assert
        broadcast.Events.Should().Equal("App.Events.OrderShipped");
    }

    [TestMethod]
    public async Task ExcludeStructMessages()
    {
        // Arrange -- Conduit's attributes are class-only, so a struct implementing a message interface is not a message:
        // neither it nor its handler or listener is emitted, beside a class command that is
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Conduit.Handlers;
            using Conduit.Messages;

            namespace App
            {
                public readonly record struct Ping(int Sequence) : ICommand;

                public sealed class PingHandler : ICommandHandler<Ping>
                {
                    public Task HandleAsync(Ping command, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public readonly record struct Pinged(int Sequence) : IEvent;

                public sealed class PingedListener : IEventListener<Pinged>
                {
                    public Task HandleAsync(Pinged @event, CancellationToken cancellationToken) => Task.CompletedTask;
                }

                public sealed record Pong(int Sequence) : ICommand<int>;

                public sealed class PongHandler : ICommandHandler<Pong, int>
                {
                    public Task<int> HandleAsync(Pong command, CancellationToken cancellationToken) => Task.FromResult(command.Sequence);
                }
            }
            """;

        // Act
        var (output, diagnostics) = RunEveryEmitter(source);
        var generated = LoadOutput(output);
        await using var services = BuildGeneratedServices(generated, new RecordingTransport());
        var answer = await services.GetRequiredService<IMediator>().ExecuteAsync((ICommand<int>)NewMessage(generated, "App.Pong", 7));

        // Assert
        GeneratedText(output).Should().NotContain("Ping");
        diagnostics.Should().NotContain(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("Ping"));
        answer.Should().Be(7);
    }

    // Runs the generated AddConduit (with configure) on a container with logging and one broadcast transport.
    private static ServiceProvider BuildGeneratedServices(
        Assembly generated,
        IConduitTransport broadcastTransport,
        Action<ConduitBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        generated.GetType("Conduit.Generated.ConduitRegistration", throwOnError: true)!
            .GetMethod("AddConduit", [typeof(IServiceCollection), typeof(Action<ConduitBuilder>)])!
            .Invoke(null, [services, configure]);
        services.AddSingleton<IConduitTransport>(broadcastTransport);
        return services.BuildServiceProvider();
    }

    private static object NewMessage(Assembly generated, string fullTypeName, params object[] arguments)
        => Activator.CreateInstance(generated.GetType(fullTypeName, throwOnError: true)!, arguments)!;

    // ConduitEndpointBuilder.RouteType<T>() for a type only the generated assembly declares.
    private static void RouteType(ConduitEndpointBuilder endpoint, Assembly generated, string fullTypeName)
        => typeof(ConduitEndpointBuilder).GetMethod(nameof(ConduitEndpointBuilder.RouteType))!
            .MakeGenericMethod(generated.GetType(fullTypeName, throwOnError: true)!)
            .Invoke(endpoint, null);

    // Every generated file's text, joined (the test's own source tree has no file path).
    private static string GeneratedText(Compilation output)
        => string.Join("\n", output.SyntaxTrees.Where(tree => tree.FilePath.Length > 0).Select(tree => tree.GetText().ToString()));

    // Runs the generator with both feature assemblies referenced, so the REST endpoints and the resilience dispatch are
    // emitted -- and compiled -- alongside the dispatch, registration and mediator.
    private static (Compilation Output, System.Collections.Immutable.ImmutableArray<Diagnostic> Generator) RunEveryEmitter(
        string source,
        params MetadataReference[] extraReferences)
        => RunGenerator(source, [HttpTransportReference, ResilienceReference, .. extraReferences]);
}
