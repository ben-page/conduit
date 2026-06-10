using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Generated;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
[DoNotParallelize]
public class MessageContextPropagationShould
{
    private ServiceProvider? serviceProvider;

    [TestInitialize]
    public void Setup()
    {
        CapturedContext.Reset();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit(conduit =>
        {
            conduit.AddPipeline(typeof(ContextCapturingBehavior<,>));
        });
        this.serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        this.serviceProvider?.Dispose();
    }

    [TestMethod]
    public async Task SetContextDuringCommandDispatch()
    {
        // Arrange
        var mediator = this.serviceProvider!.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("test"));

        // Assert
        CapturedContext.Last.Should().NotBeNull();
        CapturedContext.Last!.MessageId.Should().NotBeNullOrEmpty();
        CapturedContext.Last.ConversationId.Should().Be(CapturedContext.Last.MessageId);
        CapturedContext.Last.CausationId.Should().Be(CapturedContext.Last.MessageId);
    }

    [TestMethod]
    public async Task ReturnNullContextAfterDispatchCompletes()
    {
        // Arrange
        var mediator = this.serviceProvider!.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("test"));

        // Assert
        MessageContext.Current.Should().BeNull();
    }

    [TestMethod]
    public async Task PropagateContextThroughNestedDispatchChain()
    {
        // Arrange
        // Chain: TopLevelCommand → NestedEventOne + NestedEventTwo → DeeplyNestedCommand
        var mediator = this.serviceProvider!.GetRequiredService<IMediator>();

        // Act
        await mediator.ExecuteAsync(new TopLevelCommand("test"));

        // Assert
        var topLevel = CapturedContext.ForType(typeof(TopLevelCommand).FullName!);
        var eventOne = CapturedContext.ForType(typeof(NestedEventOne).FullName!);
        var eventTwo = CapturedContext.ForType(typeof(NestedEventTwo).FullName!);
        var deeplyNested = CapturedContext.ForType(typeof(DeeplyNestedCommand).FullName!);

        topLevel.Should().NotBeNull();
        eventOne.Should().NotBeNull();
        eventTwo.Should().NotBeNull();
        deeplyNested.Should().NotBeNull();

        // All share the same ConversationId
        var conversationId = topLevel!.ConversationId;
        eventOne!.ConversationId.Should().Be(conversationId);
        eventTwo!.ConversationId.Should().Be(conversationId);
        deeplyNested!.ConversationId.Should().Be(conversationId);

        // TopLevelCommand is the root — self-causates: CausationId == MessageId == ConversationId
        topLevel.CausationId.Should().Be(topLevel.MessageId);
        topLevel.ConversationId.Should().Be(topLevel.MessageId);

        // Both events were caused by the TopLevelCommand
        eventOne.CausationId.Should().Be(topLevel.MessageId);
        eventTwo.CausationId.Should().Be(topLevel.MessageId);

        // DeeplyNestedCommand was caused by NestedEventTwo (dispatched from its listener)
        deeplyNested.CausationId.Should().Be(eventTwo.MessageId);

        // All MessageIds are unique
        var messageIds = new[]
        {
            topLevel.MessageId,
            eventOne.MessageId,
            eventTwo.MessageId,
            deeplyNested.MessageId,
        };
        messageIds.Distinct().Should().HaveCount(4);
    }

    [TestMethod]
    public async Task StartSeparateConversationsForIndependentDispatches()
    {
        // Arrange
        var mediator = this.serviceProvider!.GetRequiredService<IMediator>();

        // Act — two independent dispatches from a clean scope
        await mediator.ExecuteAsync(new EchoCommand("first"));
        var firstConversation = CapturedContext.Last!.ConversationId;

        await mediator.ExecuteAsync(new EchoCommand("second"));
        var secondConversation = CapturedContext.Last!.ConversationId;

        // Assert — each dispatch starts its own conversation
        firstConversation.Should().NotBe(secondConversation);
    }
}
