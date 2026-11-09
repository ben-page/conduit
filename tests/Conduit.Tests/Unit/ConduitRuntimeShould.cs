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

        // Assert
        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*no transport configured*");
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

    public Task RedeliverAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
