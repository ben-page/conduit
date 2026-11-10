using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Conduit.Tests.Support.RouteMatching.Orders;
using Conduit.Tests.Support.RouteMatching.OrdersArchive;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitRuntimeShould
{
    [TestMethod]
    public void ReturnNullWhenNoRoutesConfigured()
    {
        var options = new ConduitOptions();

        var result = ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        result.Should().BeNull();
    }

    [TestMethod]
    public void MatchNamespacePrefix()
    {
        var options = new ConduitOptions();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };
        options.Endpoints["orders-api"] = endpoint;
        options.Routes.Add(new ConduitRoute("Orders.Commands", "orders-api", Exact: false));

        var result = ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        result.Should().NotBeNull();
        result.Name.Should().Be("orders-api");
    }

    [TestMethod]
    public void ReturnNullWhenNoPatternMatches()
    {
        var options = new ConduitOptions();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };
        options.Endpoints["orders-api"] = endpoint;
        options.Routes.Add(new ConduitRoute("Orders.Commands", "orders-api", Exact: false));

        var result = ConduitRuntime.ResolveRoute(options, "Billing.Commands.ChargeCard");

        result.Should().BeNull();
    }

    [TestMethod]
    public void PickFirstMatchWhenMultipleRoutesMatch()
    {
        // Arrange -- "Orders" covers "Orders.Commands", so both routes match a command in it
        var options = new ConduitOptions();
        options.Endpoints["specific"] = new ConduitEndpoint { Name = "specific" };
        options.Endpoints["general"] = new ConduitEndpoint { Name = "general" };
        options.Routes.Add(new ConduitRoute("Orders.Commands", "specific", Exact: false));
        options.Routes.Add(new ConduitRoute("Orders", "general", Exact: false));

        // Act
        var bothMatch = ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");
        var onlySecondMatches = ConduitRuntime.ResolveRoute(options, "Orders.Queries.GetOrder");

        // Assert
        bothMatch!.Name.Should().Be("specific");
        onlySecondMatches!.Name.Should().Be("general");
    }

    [TestMethod]
    public void NotRouteASiblingTypeByPrefix()
    {
        // Arrange -- CreateOrderLine's full name starts with CreateOrder's
        var builder = new ConduitBuilder(new ServiceCollection());
        builder.AddEndpoint("orders-api", endpoint => endpoint.RouteType<CreateOrder>());

        // Act
        var routed = ConduitRuntime.ResolveRoute(builder.Options, typeof(CreateOrder).FullName!);
        var sibling = ConduitRuntime.ResolveRoute(builder.Options, typeof(CreateOrderLine).FullName!);

        // Assert
        routed!.Name.Should().Be("orders-api");
        sibling.Should().BeNull();
    }

    [TestMethod]
    public void NotRouteASiblingNamespaceByPrefix()
    {
        // Arrange -- the OrdersArchive namespace's name starts with the Orders namespace's
        var builder = new ConduitBuilder(new ServiceCollection());
        builder.AddEndpoint("orders-api", endpoint => endpoint.RouteNamespace<CreateOrder>());
        var ordersNamespace = typeof(CreateOrder).Namespace!;

        // Act
        var inNamespace = ConduitRuntime.ResolveRoute(builder.Options, typeof(CreateOrderLine).FullName!);
        var inChildNamespace = ConduitRuntime.ResolveRoute(builder.Options, $"{ordersNamespace}.Lines.AddLine");
        var inSiblingNamespace = ConduitRuntime.ResolveRoute(builder.Options, typeof(ArchiveOrder).FullName!);

        // Assert
        inNamespace!.Name.Should().Be("orders-api");
        inChildNamespace!.Name.Should().Be("orders-api");
        inSiblingNamespace.Should().BeNull();
    }

    [TestMethod]
    public void ThrowWhenEndpointNotRegistered()
    {
        var options = new ConduitOptions();
        options.Routes.Add(new ConduitRoute("Orders", "missing", Exact: false));

        var action = () => ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*endpoint 'missing' is not registered*");
    }

    [TestMethod]
    public void ThrowWhenEndpointHasNoTransport()
    {
        // Arrange — endpoint exists but no keyed IConduitTransport registered
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };

        // Act
        var action = () => ConduitRuntime.ResolveTransport(serviceProvider, endpoint);

        // Assert -- the hint names no particular transport: every transport package has its own Use* extension
        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("Endpoint 'orders-api' has no transport configured. Call the transport's Use* extension (e.g. UseRabbitMQ) on the endpoint in AddEndpoint(\"orders-api\", ...).");
    }

    [TestMethod]
    public void RefuseToCastATaskToAnotherResultType()
    {
        // Arrange -- reinterpreting a Task<string> as a Task<int> would read the string's reference as an int
        var task = Task.FromResult("not a number");

        // Act
        Action cast = () => ConduitRuntime.CastTask<string, int>(task);

        // Assert
        cast.Should().ThrowExactly<InvalidCastException>();
    }

    [TestMethod]
    public async Task CastATaskToItsOwnResultType()
    {
        // Arrange
        var task = Task.FromResult(42);

        // Act
        var result = await ConduitRuntime.CastTask<int, int>(task);

        // Assert
        result.Should().Be(42);
    }

    [TestMethod]
    public void LogPipelines_names_the_type_and_the_count_in_their_places()
    {
        // Arrange
        var logger = new RecordingLogger<ConduitRuntimeShould>();

        // Act
        ConduitRuntime.LogPipelines(logger, "Orders.Commands.CreateOrder", 3);

        // Assert
        logger.Entries.Should().ContainSingle()
            .Which.Message.Should().Be("Executing pipeline behaviors for 'Orders.Commands.CreateOrder': 3");
    }

    [TestMethod]
    public async Task PropagateClientCancellationWithoutLoggingAnError()
    {
        // Arrange -- the caller gives up while the handler runs
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        using var cancellation = new CancellationTokenSource();
        var envelope = new DispatchEnvelope("Test.Slow", JsonSerializer.SerializeToElement(new { }));

        // Act
        var dispatch = () => ConduitRuntime.HandleWireRequest(
            envelope.Type,
            [],
            () => envelope,
            async (_, _, cancellationToken) =>
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            },
            logger,
            cancellation.Token);

        // Assert -- the cancellation reaches the caller: no answer, no support reference, no Error line
        await dispatch.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [TestMethod]
    public async Task PropagateAWireEventsClientCancellationWithoutLoggingAnError()
    {
        // Arrange
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        using var cancellation = new CancellationTokenSource();
        var envelope = new DispatchEnvelope("Test.Slow", JsonSerializer.SerializeToElement(new { }));

        // Act
        var publish = () => ConduitRuntime.HandleWireEvent(
            envelope.Type,
            [],
            () => envelope,
            async (_, _, cancellationToken) =>
            {
                await cancellation.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            },
            logger,
            cancellation.Token);

        // Assert
        await publish.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
    }

    [TestMethod]
    public async Task AnswerACancellationTheCallerDidNotAskForAsAnUnexpectedError()
    {
        // Arrange -- the handler's own operation timed out; the caller's token is live
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        var envelope = new DispatchEnvelope("Test.TimedOut", JsonSerializer.SerializeToElement(new { }));

        // Act
        var result = await ConduitRuntime.HandleWireRequest(
            envelope.Type,
            [],
            () => envelope,
            (_, _, _) => throw new TaskCanceledException("The request timed out."),
            logger,
            CancellationToken.None);

        // Assert
        result.Error!.Code.Should().Be("InternalError");
        result.Error.Reference.Should().NotBeNullOrEmpty();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error);
    }

    // A failure outside the listeners is no refusal and no handled event: logged once, with its stack and a support
    // reference, and thrown typed with that reference, for the transport to settle (it used to be logged and swallowed)
    [TestMethod]
    public async Task ThrowAWireEventsUnexpectedFailureWithTheReferenceItWasLoggedUnder()
    {
        // Arrange -- a behavior's failure, as it reaches HandleWireEvent from the generated dispatch
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        var envelope = new DispatchEnvelope("Test.Failing", JsonSerializer.SerializeToElement(new { }));
        var original = new InvalidOperationException("The behavior failed.");

        // Act
        var publish = () => ConduitRuntime.HandleWireEvent(
            envelope.Type,
            [],
            () => envelope,
            (_, _, _) => throw original,
            logger,
            CancellationToken.None);

        // Assert -- the original inside, its reference the one the single Error line carries
        var failure = (await publish.Should().ThrowExactlyAsync<EventDispatchException>()).Which;
        failure.InnerException.Should().BeSameAs(original);
        failure.MessageType.Should().Be("Test.Failing");
        failure.Reference.Should().MatchRegex("^[0-9a-f]{32}$");
        var error = logger.Entries.Should().ContainSingle(entry => entry.Level >= LogLevel.Warning).Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.EventId.Id.Should().Be(2);
        error.EventId.Name.Should().Be("PublishError");
        error.Message.Should().Contain(failure.Reference);
        error.Exception.Should().BeSameAs(original);
    }

    // A gate that throws something other than a refusal (an inbox whose store is down) decided nothing: the same
    [TestMethod]
    public async Task ThrowAWireEventsGateFailureAsAnUnexpectedFailure()
    {
        // Arrange
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        var envelope = new DispatchEnvelope("Test.Gated", JsonSerializer.SerializeToElement(new { }));
        var dispatched = false;

        // Act
        var publish = () => ConduitRuntime.HandleWireEvent(
            envelope.Type,
            [new ThrowingGate()],
            () => envelope,
            (_, _, _) =>
            {
                dispatched = true;
                return Task.CompletedTask;
            },
            logger,
            CancellationToken.None);

        // Assert
        var failure = (await publish.Should().ThrowExactlyAsync<EventDispatchException>()).Which;
        failure.InnerException.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("The inbox store is unreachable.");
        dispatched.Should().BeFalse();
        logger.Entries.Should().ContainSingle(entry => entry.Level == LogLevel.Error && entry.EventId.Id == 2);
    }

    // A listener's failure reaching an edge (HTTP /publish, the hub's Publish) is answered as any unexpected failure:
    // a reference minted there, logged with the first listener failure, and the InternalError answer carrying it
    [TestMethod]
    public void AnswerAListenerFailureAtAnEdgeAsAnUnexpectedFailureLoggedWithTheFirstListenersException()
    {
        // Arrange
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        var first = new InvalidOperationException("The first listener failed.");
        var failure = new EventListenerException(
            "One or more listeners failed.", [first, new InvalidOperationException("The second listener failed.")]);

        // Act
        var result = ConduitRuntime.UnexpectedEventFailure(logger, "Test.Failing", failure);

        // Assert -- the InternalError answer, its reference the one the single Error line carries with the first failure
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("InternalError");
        result.Error.Message.Should().Be("An unexpected error occurred.");
        result.Error.Reference.Should().MatchRegex("^[0-9a-f]{32}$");
        var error = logger.Entries.Should().ContainSingle().Subject;
        error.Level.Should().Be(LogLevel.Error);
        error.EventId.Id.Should().Be(2);
        error.EventId.Name.Should().Be("PublishError");
        error.Message.Should().Contain("Test.Failing").And.Contain(result.Error.Reference);
        error.Exception.Should().BeSameAs(first);
    }

    // An EventListenerException built with no listener failures has no inner exception: the line carries it instead
    [TestMethod]
    public void LogAListenerFailureWithNoInnerExceptionAsItself()
    {
        // Arrange
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        var failure = new EventListenerException("No listener named its failure.", []);

        // Act
        ConduitRuntime.UnexpectedEventFailure(logger, "Test.Failing", failure);

        // Assert
        logger.Entries.Should().ContainSingle().Which.Exception.Should().BeSameAs(failure);
    }

    [TestMethod]
    public async Task RethrowARefusedWireEventAndLogItAtWarning()
    {
        // Arrange -- a gate refuses the event, as the type filter refuses an unlisted one
        var logger = new RecordingLogger<ConduitRuntimeShould>();
        var envelope = new DispatchEnvelope("Test.Unlisted", JsonSerializer.SerializeToElement(new { }));
        var dispatched = false;

        // Act
        var publish = () => ConduitRuntime.HandleWireEvent(
            envelope.Type,
            [new RefusingGate()],
            () => envelope,
            (_, _, _) =>
            {
                dispatched = true;
                return Task.CompletedTask;
            },
            logger,
            CancellationToken.None);

        // Assert -- the refusal reaches the caller, logged as a refusal without a stack, never as an error
        await publish.Should().ThrowExactlyAsync<ForbiddenException>().WithMessage("Not this one.");
        dispatched.Should().BeFalse();
        logger.Entries.Should().ContainSingle(entry => entry.Level >= LogLevel.Warning)
            .Which.Should().Match<(LogLevel Level, EventId EventId, string Message, Exception? Exception)>(entry =>
                entry.Level == LogLevel.Warning
                && entry.EventId.Id == 7
                && entry.EventId.Name == "RefusedEvent"
                && entry.Exception == null);
    }

    [TestMethod]
    public async Task TagWireRequestsAsRequest()
    {
        // Arrange -- a root activity of this test's own, so the listener keeps only this trace: methods run in
        // parallel, and other tests' Conduit activities reach a process-wide listener.
        using var root = new Activity("ConduitRuntimeShould.TagWireRequestsAsRequest").Start();
        var traceId = root.TraceId;
        var kinds = new List<object?>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Conduit",
            Sample = static (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == traceId && activity.OperationName == "ConduitRuntime/HandleWireRequest")
                {
                    kinds.Add(activity.GetTagItem("conduit.message.kind"));
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var envelope = new DispatchEnvelope("Test.SomeQuery", JsonSerializer.SerializeToElement(new { }));

        // Act -- the wire cannot tell a command from a query by its name alone
        await ConduitRuntime.HandleWireRequest(
            envelope.Type,
            [],
            () => envelope,
            (_, _, _) => Task.FromResult<object?>("answer"),
            new RecordingLogger<ConduitRuntimeShould>(),
            CancellationToken.None);

        // Assert
        kinds.Should().Equal("request");
    }

    private sealed class RefusingGate : IInboundInterceptor
    {
        public Task<bool> ShouldProcessAsync(string messageType, CancellationToken cancellationToken)
            => throw new ForbiddenException("Not this one.");
    }

    [TestMethod]
    public void ResolveTransportFromKeyedService()
    {
        // Arrange
        var stubTransport = new StubTransport();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConduitTransport>("orders-api", stubTransport);
        var serviceProvider = services.BuildServiceProvider();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };

        // Act
        var result = ConduitRuntime.ResolveTransport(serviceProvider, endpoint);

        // Assert
        result.Should().BeSameAs(stubTransport);
    }
}

file class StubTransport : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;

    public Task RedeliverAsync(DispatchEnvelope envelope, RedeliveryIds? ids, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
