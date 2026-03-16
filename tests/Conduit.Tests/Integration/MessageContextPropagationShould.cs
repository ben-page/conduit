using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

[TestClass]
[DoNotParallelize]
public class MessageContextPropagationShould
{
    [TestInitialize]
    public void Setup()
    {
        CapturedContext.Reset();
    }

    [TestMethod]
    public async Task SetContextDuringCommandDispatch()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("test"));

        // Assert
        CapturedContext.Last.Should().NotBeNull();
        CapturedContext.Last!.MessageId.Should().NotBeNullOrEmpty();
        CapturedContext.Last.ConversationId.Should().Be(CapturedContext.Last.MessageId);
        CapturedContext.Last.CausationId.Should().BeNull();
    }

    [TestMethod]
    public async Task ReturnNullContextAfterDispatchCompletes()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act
        await mediator.ExecuteAsync(new EchoCommand("test"));

        // Assert
        MessageContext.Current.Should().BeNull();
    }

    [TestMethod]
    public async Task SetContextDuringEventPublish()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "test"));

        // Assert
        CapturedContext.Last.Should().NotBeNull();
        CapturedContext.Last!.MessageId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task PropagateContextThroughNestedDispatchChain()
    {
        // Arrange — CreateItemHandler publishes ItemCreatedEvent + ItemDeletedEvent.
        // ItemDeletedListener dispatches EchoCommand.
        // Chain: CreateItemCommand -> ItemCreatedEvent -> ItemDeletedEvent -> EchoCommand
        var mediator = CreateMediator();

        // Act
        await mediator.ExecuteAsync(new CreateItemCommand("test"));

        // Assert — collect contexts by type
        var commandContext = CapturedContext.ForType("Conduit.Tests.Support.CreateItemCommand");
        var createdEventContext = CapturedContext.ForType("Conduit.Tests.Support.ItemCreatedEvent");
        var deletedEventContext = CapturedContext.ForType("Conduit.Tests.Support.ItemDeletedEvent");
        var echoContext = CapturedContext.ForType("Conduit.Tests.Support.EchoCommand");

        commandContext.Should().NotBeNull();
        createdEventContext.Should().NotBeNull();
        deletedEventContext.Should().NotBeNull();
        echoContext.Should().NotBeNull();

        // All share the same ConversationId (rooted at the original command)
        var conversationId = commandContext!.ConversationId;
        createdEventContext!.ConversationId.Should().Be(conversationId);
        deletedEventContext!.ConversationId.Should().Be(conversationId);
        echoContext!.ConversationId.Should().Be(conversationId);

        // The original command has no parent
        commandContext.CausationId.Should().BeNull();
        commandContext.ConversationId.Should().Be(commandContext.MessageId);

        // Both events were caused by the command
        createdEventContext.CausationId.Should().Be(commandContext.MessageId);
        deletedEventContext.CausationId.Should().Be(commandContext.MessageId);

        // The EchoCommand was caused by the ItemDeletedEvent (dispatched from its listener)
        echoContext.CausationId.Should().Be(deletedEventContext.MessageId);

        // All MessageIds are unique
        var messageIds = new[]
        {
            commandContext.MessageId,
            createdEventContext.MessageId,
            deletedEventContext.MessageId,
            echoContext.MessageId,
        };
        messageIds.Distinct().Should().HaveCount(4);
    }

    [TestMethod]
    public async Task StartSeparateConversationsForIndependentDispatches()
    {
        // Arrange
        var mediator = CreateMediator();

        // Act — two independent dispatches from a clean scope
        await mediator.ExecuteAsync(new EchoCommand("first"));
        var firstConversation = CapturedContext.Last!.ConversationId;

        await mediator.ExecuteAsync(new EchoCommand("second"));
        var secondConversation = CapturedContext.Last!.ConversationId;

        // Assert — each dispatch starts its own conversation
        firstConversation.Should().NotBe(secondConversation);
    }

    private static IMediator CreateMediator()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
        builder.Services.AddConduit(conduit =>
        {
            conduit.AddPipeline(typeof(ContextCapturingBehavior<,>));
        });
        var app = builder.Build();
        return app.Services.CreateScope().ServiceProvider.GetRequiredService<IMediator>();
    }
}
