using AwesomeAssertions;
using Conduit.Transport.IntegrationTests.Support;
using Conduit.Transport.IntegrationTests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.IntegrationTests.RabbitMQ;

/// <summary>
/// Tests query routing over RabbitMQ: correct endpoint delivery, response data,
/// error propagation, and timeout behavior.
/// </summary>
/// <remarks>
/// Mirrors <see cref="RabbitMqCommandRoutingShould"/> but exercises the query path
/// (FetchAsync) instead of commands. Same topology and amplification caveats apply.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class RabbitMqQueryRoutingShould
{
    private static readonly RabbitMqFixture Fixture = new();

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
            Assert.Inconclusive("RabbitMQ container not available. Ensure Docker/Podman is running.");
        }
    }

    [TestMethod]
    public async Task RouteQueryToCorrectEndpoint()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        var handlerHost = await Fixture.CreateHostAsync(handlerName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var result = await mediator.FetchAsync(new TestQuery("hello"));

        // Assert — query was routed to the handler endpoint and response came back
        result.Value.Should().Be("result:hello");
        await handlerSpy.WaitForCountAsync(1);
        handlerSpy.Invocations.OfType<TestQuery>().Should().Contain(
            query => query.Input == "hello");
    }

    [TestMethod]
    public async Task ReturnQueryResponseWithCorrectData()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        await Fixture.CreateHostAsync(handlerName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var result = await mediator.FetchAsync(new TestQuery("round-trip"));

        // Assert
        result.Value.Should().Be("result:round-trip");
    }

    [TestMethod]
    public async Task PropagateConduitExceptionFromQuery()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        await Fixture.CreateHostAsync(handlerName);
        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var action = () => mediator.FetchAsync(new ThrowingQuery("missing"));

        // Assert — ConduitException (NotFound) propagated as InvalidOperationException with error code
        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.WithMessage("*NotFound*");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task TimeoutQueryWhenNoHandler()
    {
        // Arrange
        EnsureAvailable();

        var senderName = TransportTestFixture.UniqueAppName("sender");

        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var action = () => mediator.FetchAsync(new TestQuery("nobody-home"));

        // Assert — the 30s hardcoded timeout in RabbitMQ transport causes TaskCanceledException
        await action.Should().ThrowAsync<Exception>();
    }
}
