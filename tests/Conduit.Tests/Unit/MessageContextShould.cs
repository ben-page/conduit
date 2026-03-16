using AwesomeAssertions;
using Conduit.Dispatch;

namespace Conduit.Tests.Unit;

[TestClass]
public class MessageContextShould
{
    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    [TestMethod]
    public void ReturnNullWhenNoDispatchActive()
    {
        // Assert
        MessageContext.Current.Should().BeNull();
    }

    [TestMethod]
    public void CreateNewContextWithUniqueMessageId()
    {
        // Act
        var context = MessageContext.StartDispatch();

        // Assert
        context.MessageId.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void SetConversationIdToMessageIdForFirstDispatch()
    {
        // Act
        var context = MessageContext.StartDispatch();

        // Assert
        context.ConversationId.Should().Be(context.MessageId);
        context.CausationId.Should().BeNull();
    }

    [TestMethod]
    public void InheritConversationIdFromParent()
    {
        // Arrange
        var parent = MessageContext.StartDispatch();
        var parentMessageId = parent.MessageId;
        var parentConversationId = parent.ConversationId;

        // Act
        var child = MessageContext.StartDispatch();

        // Assert
        child.ConversationId.Should().Be(parentConversationId);
        child.CausationId.Should().Be(parentMessageId);
        child.MessageId.Should().NotBe(parentMessageId);
    }

    [TestMethod]
    public void RestorePreviousContextAfterDispatch()
    {
        // Arrange
        var parent = MessageContext.StartDispatch();
        var parentMessageId = parent.MessageId;

        // Act
        MessageContext.StartDispatch();
        MessageContext.RestoreContext(parent);

        // Assert
        MessageContext.Current.Should().NotBeNull();
        MessageContext.Current.MessageId.Should().Be(parentMessageId);
    }

    [TestMethod]
    public void AcceptInboundValuesFromTransport()
    {
        // Act
        var context = MessageContext.StartDispatchFromTransport(
            messageId: "msg-123",
            causationId: "cause-456",
            conversationId: "conv-789");

        // Assert
        context.MessageId.Should().Be("msg-123");
        context.CausationId.Should().Be("cause-456");
        context.ConversationId.Should().Be("conv-789");
    }

    [TestMethod]
    public async Task IsolateContextAcrossConcurrentDispatches()
    {
        // Arrange
        var ids = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Act
        await Task.WhenAll(
            Task.Run(() =>
            {
                var context = MessageContext.StartDispatch();
                Thread.Sleep(10);
                ids.Add(MessageContext.Current!.MessageId);
                MessageContext.RestoreContext(null);
            }),
            Task.Run(() =>
            {
                var context = MessageContext.StartDispatch();
                Thread.Sleep(10);
                ids.Add(MessageContext.Current!.MessageId);
                MessageContext.RestoreContext(null);
            }));

        // Assert
        ids.Should().HaveCount(2);
        ids.Distinct().Should().HaveCount(2);
    }
}
