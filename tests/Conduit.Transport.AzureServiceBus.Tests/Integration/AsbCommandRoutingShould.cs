using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Tests command routing over Azure Service Bus: correct endpoint delivery, response round-trips,
/// error propagation, and timeout behavior. Covers spec matrix 1.1-1.6.
/// </summary>
/// <remarks>
/// Tests run sequentially because all hosts share the same ASB topic (via a shared
/// topology prefix). Parallel execution would cause cross-test message delivery.
///
/// Assertions enforce strict unicast semantics: exactly one handler invocation and zero
/// bystander invocations. ASB topology correctly implements shared subscriptions for
/// commands, so these should pass.
/// </remarks>
[TestClass]
public class AsbCommandRoutingShould : AsbTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AzureServiceBus.Tests.Support.Contracts";

    [TestMethod]
    public async Task RouteCommandToCorrectEndpoint()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");
        var bystanderName = UniqueAppName("bystdr");

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
        await using var senderHost = await CreateSenderHostAsync(senderName, handlerHost.TopologyPrefix!, ContractsNamespace, "broker");

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
        var bystanderName = UniqueAppName("bystdr");

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
        await using var senderHost = await CreateSenderHostAsync(senderName, handlerHost.TopologyPrefix!, ContractsNamespace, "broker");

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
        await using var senderHost = await CreateSenderHostAsync(senderName, handlerHost.TopologyPrefix!, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new UnexpectedThrowCommand("boom"));

        // Assert — generic exception wrapped as InternalError
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*InternalError*");
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
    [Timeout(90_000)]
    public async Task TimeoutWhenNoHandlerAvailable()
    {
        // Arrange — sender-only, no handler host; a short ResponseTimeout so the test doesn't block for 60s
        var senderName = UniqueAppName("sender");
        var prefix = new ConduitOptions { TopologyMode = TopologyMode.Test }.TopologyPrefix!;

        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(5));

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new TestCommand("nobody-home"));

        // Assert -- a TimeoutException naming the request, the endpoint and the timeout, never an
        // OperationCanceledException that a caller's catch for its own cancellation would swallow
        var exception = await action.Should().ThrowExactlyAsync<TimeoutException>();
        exception.WithMessage(
            "No reply to 'Conduit.Transport.AzureServiceBus.Tests.Support.Contracts.TestCommand' from endpoint 'broker' within 00:00:05.");
    }
}
