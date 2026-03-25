using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests command routing over ActiveMQ Artemis: correct endpoint delivery, response round-trips,
/// error propagation, and timeout behavior. Covers spec matrix 1.1-1.6.
/// </summary>
/// <remarks>
/// Tests run sequentially because all hosts share the same Artemis addresses (via a shared
/// topology prefix). Parallel execution would cause cross-test message delivery.
///
/// Assertions enforce strict unicast semantics: exactly one handler invocation and zero
/// bystander invocations. If these fail, the topology has a multicast bug.
/// </remarks>
[TestClass]
public class ActiveMQCommandRoutingShould : ActiveMQTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.ActiveMQ.Tests.Support.Contracts";

    [TestMethod]
    public async Task RouteCommandToCorrectEndpoint()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");
        var bystanderName = UniqueAppName("bystander");

        await using var handlerHost = await CreateHostAsync(handlerName);
        await using var bystanderHost = await CreateHostAsync(bystanderName, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        await using var senderHost = await CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var result = await mediator.ExecuteAsync(new TestCommand("hello"));

        // Assert — command was routed to exactly one handler, bystander gets nothing
        result.Value.Should().Be("handled:hello");
        await handlerSpy.WaitForCountAsync(1);

        // Allow brief settling time to catch any delayed spurious deliveries
        await Task.Delay(500);

        handlerSpy.Invocations.OfType<TestCommand>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestCommand>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task ReturnCommandResponseToCaller()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var _ = await CreateHostAsync(handlerName);
        await using var senderHost = await CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

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
        await using var bystanderHost = await CreateHostAsync(bystanderName, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        await using var senderHost = await CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        await mediator.ExecuteAsync(new TestVoidCommand("fire"));

        // Assert — handler received exactly one invocation, bystander gets nothing
        await handlerSpy.WaitForCountAsync(1);
        await Task.Delay(500);

        handlerSpy.Invocations.OfType<TestVoidCommand>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestVoidCommand>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task PropagateConduitExceptionAsFailResult()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var _ = await CreateHostAsync(handlerName);
        await using var senderHost = await CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

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

        await using var _ = await CreateHostAsync(handlerName);
        await using var senderHost = await CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

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

        await using var senderHost = await CreateSenderHostAsync(
            senderName, ContractsNamespace, "broker",
            opts => opts.ResponseTimeout = TimeSpan.FromSeconds(5));

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var action = () => mediator.ExecuteAsync(new TestCommand("nobody-home"));

        // Assert
        await action.Should().ThrowAsync<Exception>();
    }
}
