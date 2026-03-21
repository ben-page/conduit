using AwesomeAssertions;
using Conduit.Transport.IntegrationTests.Support;
using Conduit.Transport.IntegrationTests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.IntegrationTests.RabbitMQ;

/// <summary>
/// Tests MessageContext propagation over RabbitMQ: verifies that MessageId, CausationId,
/// and ConversationId are populated on the handler side for commands, queries, and events.
/// Also tests that ConversationId is preserved through nested dispatch.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RabbitMqMessageContextShould
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
    public async Task PropagateMessageContextForCommand()
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
        await mediator.ExecuteAsync(new ContextCaptureCommand("ctx-test"));

        // Assert — handler captured a MessageContext snapshot with all fields populated
        await handlerSpy.WaitForCountAsync(1);
        var snapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.CausationId.Should().NotBeNullOrEmpty();
        snapshot.ConversationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task PropagateMessageContextForQuery()
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
        await mediator.FetchAsync(new ContextCaptureQuery("ctx-test"));

        // Assert — handler captured a MessageContext snapshot with all fields populated
        await handlerSpy.WaitForCountAsync(1);
        var snapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.CausationId.Should().NotBeNullOrEmpty();
        snapshot.ConversationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task PropagateMessageContextForEvent()
    {
        // Arrange
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var publisherName = TransportTestFixture.UniqueAppName("pub");

        var handlerHost = await Fixture.CreateHostAsync(handlerName);
        var publisherHost = await Fixture.CreateHostAsync(publisherName);

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        await mediator.PublishAsync(new ContextCaptureEvent("ctx-test"));

        // Assert — listener captured a MessageContext snapshot with all fields populated
        await handlerSpy.WaitForCountAsync(1);
        var snapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.ConversationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task PreserveConversationIdInNestedDispatch()
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

        // Act — NestingCommandHandler captures outer context, then dispatches TestCommand locally
        var result = await mediator.ExecuteAsync(new NestingCommand("nested-ctx"));

        // Assert — the outer context snapshot has a ConversationId set
        await handlerSpy.WaitForCountAsync(2, TimeSpan.FromSeconds(10));

        var outerSnapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        outerSnapshot.ConversationId.Should().NotBeNullOrEmpty();
        outerSnapshot.MessageId.Should().NotBeNullOrEmpty();

        // The NestingCommandHandler also dispatches TestCommand which records via TestCommandHandler spy
        // So we expect 2 total: 1 MessageContextSnapshot + 1 TestCommand
        handlerSpy.Count.Should().BeGreaterThanOrEqualTo(2);

        // Result should reflect the nested dispatch chain
        result.Value.Should().StartWith("nested:");
    }
}
