using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.IntegrationTests.Support;
using Conduit.Transport.IntegrationTests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.IntegrationTests.ActiveMQ;

/// <summary>
/// Tests query routing over ActiveMQ Artemis: correct endpoint delivery, response data,
/// error propagation, and timeout behavior.
/// </summary>
/// <remarks>
/// Mirrors <see cref="ActiveMQCommandRoutingShould"/> but exercises the query path
/// (FetchAsync) instead of commands. Assertions enforce strict unicast semantics:
/// exactly one handler invocation and zero bystander invocations.
/// </remarks>
[TestClass]
[DoNotParallelize]
public class ActiveMQQueryRoutingShould
{
    private static readonly ActiveMQFixture Fixture = new();

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
            Assert.Inconclusive("ActiveMQ Artemis container not available. Ensure Docker/Podman is running.");
        }
    }

    [TestMethod]
    public async Task RouteQueryToCorrectEndpoint()
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

        await Task.Delay(1000);

        // Act
        var result = await mediator.FetchAsync(new TestQuery("hello"));

        // Assert — query was routed to exactly one handler, bystander gets nothing
        result.Value.Should().Be("result:hello");
        await handlerSpy.WaitForCountAsync(1);

        // Allow brief settling time to catch any delayed spurious deliveries
        await Task.Delay(500);

        handlerSpy.Invocations.OfType<TestQuery>().Should().HaveCount(1);
        bystanderSpy.Invocations.OfType<TestQuery>().Should().HaveCount(0);
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
    [Timeout(15_000)]
    public async Task TimeoutQueryWhenNoHandler()
    {
        // Arrange — use a short ResponseTimeout so the test doesn't block for 60s
        EnsureAvailable();

        var senderName = TransportTestFixture.UniqueAppName("sender");
        var senderHost = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            Fixture.ConfigureSenderTransport(
                builder, senderName, ContractsNamespace, "broker",
                opts => opts.ResponseTimeout = TimeSpan.FromSeconds(5));
        });
        Fixture.TrackHost(senderHost);

        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(1000);

        // Act
        var action = () => mediator.FetchAsync(new TestQuery("nobody-home"));

        // Assert
        await action.Should().ThrowAsync<Exception>();
    }
}
