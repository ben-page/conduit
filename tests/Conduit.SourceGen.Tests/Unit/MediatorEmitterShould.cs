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
        var (output, _) = RunGenerator(source);

        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");
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

        // The inline publish lets EventListenerException propagate; only the background enqueue (none here) catches it
        mediatorSource.Should().NotContain("catch (Conduit.Exceptions.EventListenerException ex)");

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
    public void BindEveryDispatchContextToItsAttemptRightAfterStartingIt()
    {
        // Arrange
        const string source = """
            using Conduit.Handlers;
            using Conduit.Messages;
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace App;

            public record CreateOrder(string CustomerId) : ICommand<Guid>;

            public record DeleteOrder(Guid Id) : ICommand;

            public record GetOrder(Guid Id) : IQuery<string>;

            public record OrderCreated(Guid Id) : IEvent;

            public class CreateOrderHandler : ICommandHandler<CreateOrder, Guid>
            {
                public Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken)
                    => Task.FromResult(Guid.NewGuid());
            }

            public class DeleteOrderHandler : ICommandHandler<DeleteOrder>
            {
                public Task HandleAsync(DeleteOrder command, CancellationToken cancellationToken) => Task.CompletedTask;
            }

            public class GetOrderHandler : IQueryHandler<GetOrder, string>
            {
                public Task<string> HandleAsync(GetOrder query, CancellationToken cancellationToken)
                    => Task.FromResult("order");
            }
            """;
        const string attach =
            "Conduit.Runtime.MessageContext.Current?.AttachObserver(serviceProvider.GetService<Conduit.Runtime.IConduitObserver>());";
        const string bind = "Conduit.Runtime.ConduitRuntime.BindDispatchAttempt(Conduit.Runtime.MessageContext.Current, serviceProvider);";

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator")!;
        var lines = mediatorSource.Split('\n').Select(line => line.Trim()).ToList();

        // Assert -- every context start (the in-process one per ExecuteAsync/FetchAsync/PublishAsync, the wire one
        // per DispatchRequestAsync/DispatchEventAsync, and the outbox delivery's PublishLocalEnvelopeAsync) binds the context on the line after it attaches the observer
        var attachLines = lines.Select((line, index) => (line, index)).Where(entry => entry.line == attach).ToList();
        attachLines.Should().HaveCount(7, "three typed overloads, PublishAsync, the two wire dispatch methods, and the outbox's local delivery");
        foreach (var (_, index) in attachLines)
        {
            lines[index + 1].Should().Be(bind);
        }

        lines.Count(line => line == bind).Should().Be(attachLines.Count);

        var wireRequest = mediatorSource[mediatorSource.IndexOf("private async Task<object?> DispatchRequestAsync(", StringComparison.Ordinal)..];
        wireRequest.IndexOf(bind, StringComparison.Ordinal)
            .Should().BeLessThan(wireRequest.IndexOf("ConduitDispatch.Execute(", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StartEveryInProcessContextInsideATryThatRestoresTheCaller()
    {
        // Arrange -- the audit capture serializes the caller's object, and the observer can throw
        const string source = """
            using Conduit.Handlers;
            using Conduit.Messages;
            using System;
            using System.Threading;
            using System.Threading.Tasks;

            namespace App;

            public record CreateOrder(string CustomerId) : ICommand<Guid>;

            public record OrderCreated(Guid Id) : IEvent;

            public class CreateOrderHandler : ICommandHandler<CreateOrder, Guid>
            {
                public Task<Guid> HandleAsync(CreateOrder command, CancellationToken cancellationToken)
                    => Task.FromResult(Guid.NewGuid());
            }
            """;

        // Act
        var (output, _) = RunGenerator(source);
        var lines = FindGeneratedSource(output, "ConduitMediator")!.Split('\n').Select(line => line.Trim()).ToList();

        // Assert -- in ExecuteAsync and PublishAsync alike, everything from StartDispatch to DispatchInvoked (the
        // capture included) runs in a try whose catch restores the caller's context and rethrows, before the
        // dispatch's own try begins
        var starts = lines.Select((line, index) => (line, index))
            .Where(entry => entry.line.Contains(".StartDispatch(__dispatchOptions)", StringComparison.Ordinal))
            .Select(entry => lines.FindLastIndex(entry.index, line => line.StartsWith("var previousContext = ", StringComparison.Ordinal)))
            .ToList();
        starts.Should().HaveCount(2, "the CreateOrder overload and PublishAsync each start a dispatch's context");
        foreach (var start in starts)
        {
            var block = lines.Skip(start).TakeWhile(line => !line.StartsWith("using var __activity", StringComparison.Ordinal)).ToList();
            var catchAt = block.IndexOf("catch");
            block[1].Should().Be("try");
            block.FindIndex(line => line.Contains(".StartDispatch(", StringComparison.Ordinal)).Should().BeInRange(2, catchAt);
            block.FindIndex(line => line.Contains("ConduitRuntime.AuditJsonOptions", StringComparison.Ordinal)).Should().BeInRange(2, catchAt);
            block.FindIndex(line => line.Contains("DispatchInvoked(", StringComparison.Ordinal)).Should().BeInRange(2, catchAt);
            block.Skip(catchAt).Should().ContainInOrder(
                "catch", "{", "Conduit.Runtime.MessageContext.RestoreContext(previousContext);", "throw;", "}");
        }
    }

    [TestMethod]
    public void PassTheServiceProviderToTheWire()
    {
        // Arrange
        const string source = """
            using Conduit.Handlers;
            using Conduit.Messages;
            using System.Threading;
            using System.Threading.Tasks;

            namespace App.Commands
            {
                public record Ping : ICommand;

                public class PingHandler : ICommandHandler<Ping>
                {
                    public Task HandleAsync(Ping command, CancellationToken cancellationToken) => Task.CompletedTask;
                }
            }
            """;

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert -- the runtime resolves translators, the renderer and the culture resolver itself
        mediatorSource.Should().Contain("=> ConduitRuntime.HandleWireRequest(envelope.Type, interceptors, () => envelope, DispatchRequestAsync, logger, serviceProvider, cancellationToken);");
        mediatorSource.Should().NotContain("GetServices<Conduit.Runtime.IWireExceptionTranslator>()");
    }

    [TestMethod]
    public void ReadWirePayloadsThroughTheRuntimeHelper()
    {
        // Arrange -- the same compilation EmitMediatorWithStronglyTypedOverloads uses (CreateOrder/DeleteOrder/GetOrder)
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

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert -- a raw Deserialize here would turn an unreadable payload into InternalError (a 500)
        mediatorSource.Should().MatchRegex(@"DeserializeWire<(global::)?App\.Commands\.CreateOrder>\(payload\)");
        mediatorSource.Should().MatchRegex(@"DeserializeWire<(global::)?App\.Commands\.DeleteOrder>\(payload\)");
        mediatorSource.Should().MatchRegex(@"DeserializeWire<(global::)?App\.Queries\.GetOrder>\(payload\)");
        mediatorSource.Should().NotMatchRegex(@"JsonSerializer\.Deserialize<(global::)?App\.(Commands|Queries)\.");
    }

    [TestMethod]
    public void ResolveListenersForAnEventTypeThatHasNoneAtCompileTime()
    {
        // Arrange — an event nothing in the compilation listens to
        const string source = """
            using Conduit.Messages;

            namespace TestApp;

            public record CartAbandoned(string CartId) : IEvent;
            """;

        // Act
        var (output, _) = RunGenerator(source);

        // Assert — the case resolves listeners from the container, so a listener registered at
        // runtime (as Conduit.Jobs does for an event trigger) still runs. Gating the case on
        // compile-time discovery would make such an event undeliverable however it was registered.
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");
        mediatorSource.Should().Contain("case \"TestApp.CartAbandoned\":");
        mediatorSource.Should().Contain(
            "GetServices<Conduit.Handlers.IEventListener<TestApp.CartAbandoned>>");
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
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert — interface on the class, classification switches keyed by METADATA names, publish entry
        mediatorSource.Should().NotBeNull();
        mediatorSource.Should().Contain("ILocalEnvelopeDispatcher");
        // (scoped to each switch: the broadcast gate lists every broadcastable event "=> true" too)
        var deliversLocally = SwitchOf(mediatorSource, "public bool DeliversLocally(string messageType)");
        deliversLocally.Should().Contain("\"TestApp.CapturedEvent\" => true");         // capturable F&F
        deliversLocally.Should().NotContain("\"TestApp.ImmediateEvent\"");             // Immediate is not outbox-delivered
        var isLocalOnly = SwitchOf(mediatorSource, "public bool IsLocalOnly(string messageType)");
        isLocalOnly.Should().Contain("\"TestApp.LocalOnlyEvent\" => true");
        var isFireAndForget = SwitchOf(mediatorSource, "public bool IsFireAndForget(string fullTypeName)");
        isFireAndForget.Should().Contain("\"TestApp.CapturedEvent\" => true");
        isFireAndForget.Should().Contain("\"TestApp.ImmediateEvent\" => true");          // Immediate is still fire-and-forget
        isFireAndForget.Should().Contain("\"TestApp.Outer+NestedCapturedEvent\" => true");
        isFireAndForget.Should().NotContain("\"TestApp.LocalOnlyEvent\"");
        var isEvent = SwitchOf(mediatorSource, "public bool IsEvent(string fullTypeName)");
        isEvent.Should().Contain("\"TestApp.CapturedEvent\" => true");
        isEvent.Should().Contain("\"TestApp.ImmediateEvent\" => true");
        isEvent.Should().Contain("\"TestApp.LocalOnlyEvent\" => true");               // never routed, and not broadcast
        isEvent.Should().Contain("\"TestApp.Outer+NestedCapturedEvent\" => true");
        mediatorSource.Should().Contain("public async Task PublishLocalEnvelopeAsync(");

        // Nested types are the one case where metadata ('+') and C# ('.') forms differ: switch STRING
        // keys must be the metadata form (what DispatchEnvelope.Type carries at runtime), while the
        // Deserialize generic argument must be the C# form. Non-nested fixtures cannot catch a mixup.
        mediatorSource.Should().Contain("\"TestApp.Outer+NestedCapturedEvent\" => true");
        mediatorSource.Should().NotContain("\"TestApp.Outer.NestedCapturedEvent\" =>");
        mediatorSource.Should().Contain("\"TestApp.Outer+NestedCapturedEvent\" => JsonSerializer.Deserialize<TestApp.Outer.NestedCapturedEvent>");
        mediatorSource.Should().Contain("\"TestApp.ImmediateEvent\" => JsonSerializer.Deserialize<TestApp.ImmediateEvent>");

        // Assert — registration delegates the new interface to the same mediator instance
        var registrationSource = FindGeneratedSource(output, "ConduitRegistration");
        registrationSource.Should().NotBeNull();
        registrationSource.Should().Contain(
            "services.AddTransient<Conduit.ILocalEnvelopeDispatcher>(sp => sp.GetRequiredService<ConduitMediator>());");
    }

    [TestMethod]
    public void CatchListenerFailureOnlyInTheFireAndForgetBackgroundPath()
    {
        // Arrange — one plain event (listeners inline) and one F&F event (listeners enqueued)
        const string source = """
            using Conduit.Attributes;
            using Conduit.Messages;
            using Conduit.Handlers;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestApp;

            public record PlainEvent(string Detail) : IEvent;

            [FireAndForget]
            public record BackgroundEvent(string Detail) : IEvent;

            public class PlainListener : IEventListener<PlainEvent>
            {
                public Task HandleAsync(PlainEvent e, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert — the background lambda logs the failure; the inline path has no catch, so it propagates
        mediatorSource.Should().NotBeNull();
        const string catchClause = "catch (Conduit.Exceptions.EventListenerException ex)";
        mediatorSource!.Split(catchClause).Length.Should().Be(2, "only the F&F background lambda catches");
        mediatorSource.Should().Contain("captured.GetType().FullName");
        var catchIndex = mediatorSource.IndexOf(catchClause, StringComparison.Ordinal);
        var enqueueIndex = mediatorSource.IndexOf("dispatcher.Enqueue(", StringComparison.Ordinal);
        catchIndex.Should().BeGreaterThan(enqueueIndex);
    }

    // A transport that refuses a broadcast is not an event listener: logging it as ListenerFailed ("Event listener
    // 'SignalRTransport' threw...") sent a reader looking for a listener that does not exist.
    [TestMethod]
    public void LogATransportsBroadcastFailureAsTransportBroadcastFailed()
    {
        // Arrange
        const string source = """
            using Conduit.Messages;

            namespace TestApp;

            public record ItemShipped(string Detail) : IEvent;
            """;

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert
        mediatorSource.Should().NotBeNull();
        mediatorSource.Should().Contain(
            "ConduitRuntime.LogTransportBroadcastFailure(logger, transport.GetType().Name, eventEnvelope.Type, ex);");
        mediatorSource.Should().NotContain("LogListenerFailure(logger, transport.");
    }

    [TestMethod]
    public void EmitTransactionGatedCaptureInPublishAsync()
    {
        // Arrange — one capturable F&F event and one Immediate F&F event (both broadcastable)
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

            public class CapturedListener : IEventListener<CapturedEvent>
            {
                public Task HandleAsync(CapturedEvent e, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert — PublishAsync attempts capture BEFORE the local-enqueue/broadcast body, then guards
        // the entire body on the result so a captured event skips local enqueue AND transport send.
        mediatorSource.Should().NotBeNull();
        mediatorSource.Should().Contain("var __captured = false;");
        mediatorSource.Should().Contain("__captured = __outboundInterceptor.Intercept(");
        mediatorSource.Should().Contain("if (!__captured)");

        // The capture check is the non-Immediate F&F set only: a bare close-paren right after
        // CapturedEvent proves ImmediateEvent is NOT offered for capture (it must deliver immediately).
        mediatorSource.Should().Contain("if (@event.GetType().FullName is \"TestApp.CapturedEvent\")");

        // The broadcast bypass flag covers ALL F&F types (declined-capturable OR Immediate) so neither
        // is re-offered at the broadcast interceptor. Order is HashSet-nondeterministic -> match per type.
        mediatorSource.Should().Contain("var __bypassCapture = @event.GetType().FullName is ");
        mediatorSource.Should().MatchRegex(@"__bypassCapture = @event\.GetType\(\)\.FullName is [^;]*""TestApp\.CapturedEvent""");
        mediatorSource.Should().MatchRegex(@"__bypassCapture = @event\.GetType\(\)\.FullName is [^;]*""TestApp\.ImmediateEvent""");
    }

    [TestMethod]
    public void EmitNoCaptureAttemptWhenNoCapturableEvents()
    {
        // Arrange — only a plain (non-F&F) event: nothing is capturable, so no capture scaffold emits,
        // and the broadcast interceptor condition uses the bool return with no bypass flag.
        const string source = """
            using Conduit.Messages;
            using Conduit.Handlers;
            using System.Threading;
            using System.Threading.Tasks;

            namespace TestApp;

            public record PlainEvent(string Detail) : IEvent;

            public class PlainListener : IEventListener<PlainEvent>
            {
                public Task HandleAsync(PlainEvent e, CancellationToken ct) => Task.CompletedTask;
            }
            """;

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert — no capture guard and no bypass flag; the broadcast interceptor still uses the bool.
        mediatorSource.Should().NotBeNull();
        mediatorSource.Should().NotContain("var __captured = false;");
        mediatorSource.Should().NotContain("__bypassCapture");
        mediatorSource.Should().Contain("if (outboundInterceptor != null && outboundInterceptor.Intercept(eventEnvelope))");
    }

    [TestMethod]
    public void CaptureWirePayloadsUnlessTheTypeIsDoNotAudit()
    {
        // Arrange
        var source = @"
using Conduit.Attributes;
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

    [DoNotAudit]
    public record SignIn(string Password) : ICommand;

    public class SignInHandler : ICommandHandler<SignIn>
    {
        public Task HandleAsync(SignIn command, CancellationToken cancellationToken)
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

        // Act
        var (output, _) = RunGenerator(source);
        var mediatorSource = FindGeneratedSource(output, "ConduitMediator");

        // Assert -- request and query arms capture the typed object
        mediatorSource.Should().MatchRegex(@"ConduitRuntime\.CaptureAuditPayload\(ConduitRuntime\.DeserializeWire<(global::)?App\.Commands\.CreateOrder>\(payload\)\)");
        mediatorSource.Should().MatchRegex(@"ConduitRuntime\.CaptureAuditPayload\(ConduitRuntime\.DeserializeWire<(global::)?App\.Queries\.GetOrder>\(payload\)\)");

        // A type-level [DoNotAudit] type is read but never captured
        mediatorSource.Should().MatchRegex(@"DeserializeWire<(global::)?App\.Commands\.SignIn>\(payload\)");
        mediatorSource.Should().NotMatchRegex(@"CaptureAuditPayload\(ConduitRuntime\.DeserializeWire<(global::)?App\.Commands\.SignIn>");

        // The wire event arm captures too (it reads with JsonSerializer, not DeserializeWire)
        mediatorSource.Should().MatchRegex(@"ConduitRuntime\.CaptureAuditPayload\(JsonSerializer\.Deserialize<(global::)?App\.Events\.OrderCreated>\(payload, ConduitRuntime\.JsonOptions\)!\)");

        // The in-process capture is guarded on an attached observer
        mediatorSource.Should().Contain("is { HasObserver: true } __auditContext");
        // ... and skipped for a type the host's audit registration suppresses, which is never serialized
        mediatorSource.Should().Contain("ConduitRuntime.CapturesAuditPayload(");
        mediatorSource.Should().NotContain("MessageContext.Current?.SetPayload(");
    }

    // The emitted switch-expression member that starts with signature, through its closing "};".
    private static string SwitchOf(string? source, string signature)
    {
        source.Should().Contain(signature);
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        return source[start..source.IndexOf("};", start, StringComparison.Ordinal)];
    }
}
