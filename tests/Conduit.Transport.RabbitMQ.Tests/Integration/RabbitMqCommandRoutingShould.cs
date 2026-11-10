using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Tests command routing over RabbitMQ: correct endpoint delivery, response round-trips,
/// error propagation, and timeout behavior. Covers spec matrix 1.1-1.6.
/// </summary>
/// <remarks>
/// Tests run sequentially because all hosts share the same RabbitMQ exchange (via a shared
/// topology prefix). Parallel execution would cause cross-test message delivery.
///
/// Assertions enforce strict unicast semantics: exactly one handler invocation and zero
/// bystander invocations. If these fail, the topology has a multicast bug.
/// </remarks>
[TestClass]
public class RabbitMqCommandRoutingShould : RabbitMqTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.RabbitMQ.Tests.Support.Contracts";

    [TestMethod]
    public async Task RouteCommandToCorrectEndpoint()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");
        var bystanderName = UniqueAppName("bystander");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var bystanderHost = await CreateHostAsync(bystanderName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.ExecuteAsync(new TestCommand("hello"));

        // Assert — command was routed to exactly one handler, bystander gets nothing
        result.Value.Should().Be("handled:hello");
        await handlerSpy.WaitForCountAsync(1);

        // Allow brief settling time to catch any delayed spurious deliveries
        await Task.Delay(2000);

        handlerSpy.Invocations.OfType<TestCommand>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestCommand>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task ReturnCommandResponseToCaller()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var result = await mediator.ExecuteAsync(new TestCommand("round-trip"));

        // Assert
        result.Value.Should().Be("handled:round-trip");
    }

    [TestMethod]
    public async Task RouteVoidCommandAndComplete()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");
        var bystanderName = UniqueAppName("bystander");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var bystanderHost = await CreateHostAsync(bystanderName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new TestVoidCommand("fire"));

        // Assert — handler received exactly one invocation, bystander gets nothing
        await handlerSpy.WaitForCountAsync(1);
        await Task.Delay(2000);

        handlerSpy.Invocations.OfType<TestVoidCommand>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestVoidCommand>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task PropagateConduitExceptionAsFailResult()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new ThrowingCommand("missing"));

        // Assert — ConduitException (NotFound) propagated as InvalidOperationException with error code
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*NotFound*");
    }

    [TestMethod]
    public async Task ReturnInternalErrorForUnexpectedException()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new UnexpectedThrowCommand("boom"));

        // Assert — generic exception wrapped as InternalError
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*InternalError*");
    }

    [TestMethod]
    [Timeout(15_000)]
    public async Task TimeoutWhenNoHandlerAvailable()
    {
        // Arrange — use a short ResponseTimeout so the test doesn't block for 60s
        var senderName = UniqueAppName("sender");
        var prefix = new ConduitOptions { TopologyMode = TopologyMode.Test }.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            opts => opts.ResponseTimeout = TimeSpan.FromSeconds(5));

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new TestCommand("nobody-home"));

        // Assert -- a TimeoutException naming the request, the endpoint and the timeout, never an
        // OperationCanceledException that a caller's catch for its own cancellation would swallow
        var exception = await action.Should().ThrowExactlyAsync<TimeoutException>();
        exception.WithMessage(
            "No reply to 'Conduit.Transport.RabbitMQ.Tests.Support.Contracts.TestCommand' from endpoint 'broker' within 00:00:05.");
    }

    [TestMethod]
    public async Task DeliverFireAndForgetCommandToHandler()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act — F&F command returns immediately, no reply expected
        await mediator.ExecuteAsync(new FireAndForgetCommand("fire-and-forget"));

        // Assert — handler eventually receives and processes the command
        await handlerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(30));
        handlerSpy.Invocations.OfType<FireAndForgetCommand>().Should().ContainSingle(
            c => c.Input == "fire-and-forget");
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task KeepReplyingAfterConnectionRecovery()
    {
        // Arrange — a sender and a handler that have already exchanged one request and its reply
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(15));

        var mediator = senderHost.GetRequiredService<IMediator>();
        (await mediator.ExecuteAsync(new TestCommand("before"))).Value.Should().Be("handled:before");

        var handlerRecovered = WhenRecoveredAsync(handlerHost, "self");
        var senderRecovered = WhenRecoveredAsync(senderHost, "broker");

        // Act — restart the broker: both connections drop, and the sender's exclusive reply queue goes with
        // its connection. Once both clients have recovered their topology, send again.
        await RestartBrokerAsync();
        await Task.WhenAll(handlerRecovered, senderRecovered).WaitAsync(TimeSpan.FromSeconds(120));
        var result = await mediator.ExecuteAsync(new TestCommand("after"));

        // Assert — the reply reached the request's waiter: it went to the reply queue the sender consumes
        result.Value.Should().Be("handled:after");
    }

    /// <summary>
    /// Completes when the host's connection for <paramref name="endpointName"/> next finishes recovering. The
    /// client awaits recovery handlers in subscription order, and the transport subscribed its own (which
    /// re-declares the reply queue) when it connected, so the reply queue is back before this completes.
    /// </summary>
    private static Task WhenRecoveredAsync(TestHost host, string endpointName)
    {
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = host.Services.GetRequiredKeyedService<RabbitMQTransport>(endpointName).Connection!;
        connection.RecoverySucceededAsync += (sender, args) =>
        {
            recovered.TrySetResult();
            return Task.CompletedTask;
        };

        return recovered.Task;
    }
}
