using AwesomeAssertions;
using Conduit.Transport.IntegrationTests.Support;
using Conduit.Transport.IntegrationTests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.IntegrationTests.AzureServiceBus;

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
[DoNotParallelize]
public class AsbCommandRoutingShould
{
    private static readonly AsbFixture Fixture = new();

    private const string ContractsNamespace = "Conduit.Transport.IntegrationTests.Support.Contracts";

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        await Fixture.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await Fixture.CleanupAsync();
    }

    [TestCleanup]
    public async Task TestCleanup()
    {
        await Fixture.DisposeHostsAsync();
    }

    private static void EnsureAvailable()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Inconclusive("ASB emulator not available. Ensure Docker/Podman is running.");
        }
    }

    [TestMethod]
    public async Task RouteCommandToCorrectEndpoint()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");
        var bystanderName = TransportTestFixture.UniqueAppName("bystander");

        var handlerHost = await Fixture.CreateHostAsync(handlerName);
        var bystanderHost = await Fixture.CreateHostAsync(bystanderName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        var result = await mediator.ExecuteAsync(new TestCommand("hello"));

        // Assert — command was routed to exactly one handler, bystander gets nothing
        result.Value.Should().Be("handled:hello");
        await handlerSpy.WaitForCountAsync(1);

        // Allow brief settling time to catch any delayed spurious deliveries
        await Task.Delay(1000);

        handlerSpy.Invocations.OfType<TestCommand>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestCommand>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task ReturnCommandResponseToCaller()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        await Fixture.CreateHostAsync(handlerName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        var result = await mediator.ExecuteAsync(new TestCommand("round-trip"));

        // Assert
        result.Value.Should().Be("handled:round-trip");
    }

    [TestMethod]
    public async Task RouteVoidCommandAndComplete()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");
        var bystanderName = TransportTestFixture.UniqueAppName("bystander");

        var handlerHost = await Fixture.CreateHostAsync(handlerName);
        var bystanderHost = await Fixture.CreateHostAsync(bystanderName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var bystanderSpy = bystanderHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        await mediator.ExecuteAsync(new TestVoidCommand("fire"));

        // Assert — handler received exactly one invocation, bystander gets nothing
        await handlerSpy.WaitForCountAsync(1);
        await Task.Delay(1000);

        handlerSpy.Invocations.OfType<TestVoidCommand>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestVoidCommand>().Should().HaveCount(0);
    }

    [TestMethod]
    public async Task PropagateConduitExceptionAsFailResult()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        await Fixture.CreateHostAsync(handlerName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

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
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        await Fixture.CreateHostAsync(handlerName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        var action = () => mediator.ExecuteAsync(new UnexpectedThrowCommand("boom"));

        // Assert — generic exception wrapped as InternalError
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*InternalError*");
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task TimeoutWhenNoHandlerAvailable()
    {
        // Arrange
        EnsureAvailable();

        var senderName = TransportTestFixture.UniqueAppName("sender");

        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        var action = () => mediator.ExecuteAsync(new TestCommand("nobody-home"));

        // Assert — the hardcoded timeout in ASB transport causes TaskCanceledException
        await action.Should().ThrowAsync<Exception>();
    }
}
