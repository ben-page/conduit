using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Tests command routing over Amazon SQS: correct endpoint delivery, response round-trips,
/// error propagation, and timeout behavior. Covers spec matrix 1.1-1.6.
/// </summary>
/// <remarks>
/// Each test creates its own topology prefix via the first host, ensuring parallel-safe isolation.
///
/// Assertions enforce strict unicast semantics: exactly one handler invocation and zero
/// bystander invocations. If these fail, the topology has a multicast bug.
/// </remarks>
[TestClass]
public class SqsCommandRoutingShould : SqsTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AmazonSqs.Tests.Support.Contracts";

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

        // Assert -- command was routed to exactly one handler, bystander gets nothing
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

        // Assert -- handler received exactly one invocation, bystander gets nothing
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

        // Assert -- ConduitException (NotFound) propagated as InvalidOperationException with error code
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

        // Assert -- generic exception wrapped as InternalError
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
    [Timeout(15_000)]
    public async Task TimeoutWhenNoHandlerAvailable()
    {
        // Arrange -- use a short ResponseTimeout so the test doesn't block for 60s
        var senderName = UniqueAppName("sender");
        var prefix = new ConduitOptions { TopologyMode = TopologyMode.Test }.TopologyPrefix!;

        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            opts => opts.ResponseTimeout = TimeSpan.FromSeconds(5));

        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        var action = () => mediator.ExecuteAsync(new TestCommand("nobody-home"));

        // Assert
        await action.Should().ThrowAsync<Exception>();
    }
}
