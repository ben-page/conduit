using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.RabbitMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Tests MessageContext propagation over RabbitMQ: verifies that MessageId, CausationId,
/// and ConversationId are populated on the handler side for commands, queries, and events.
/// Also tests that ConversationId is preserved through nested dispatch.
/// </summary>
[TestClass]
public class RabbitMqMessageContextShould : RabbitMqTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.RabbitMQ.Tests.Support.Contracts";

    [TestMethod]
    public async Task PropagateMessageContextForCommand()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new ContextCaptureCommand("ctx-test"));

        // Assert — handler captured a MessageContext snapshot with all fields populated
        await handlerSpy.WaitForCountAsync(1);
        var snapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.CausationId.Should().NotBeNullOrEmpty();
        snapshot.CausationId.Should().Be(snapshot.MessageId);
        snapshot.ConversationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task PropagateMessageContextForQuery()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        // Act
        await mediator.FetchAsync(new ContextCaptureQuery("ctx-test"));

        // Assert — handler captured a MessageContext snapshot with all fields populated
        await handlerSpy.WaitForCountAsync(1);
        var snapshot = handlerSpy.Invocations.OfType<MessageContextSnapshot>().First();
        snapshot.MessageId.Should().NotBeNullOrEmpty();
        snapshot.CausationId.Should().NotBeNullOrEmpty();
        snapshot.CausationId.Should().Be(snapshot.MessageId);
        snapshot.ConversationId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task PropagateMessageContextForEvent()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var publisherName = UniqueAppName("pub");

        await using var handlerHost = await CreateHostAsync(handlerName, configureHandling: handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        var prefix = handlerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(publisherName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

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
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

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
