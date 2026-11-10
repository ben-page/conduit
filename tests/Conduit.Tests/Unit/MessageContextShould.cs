using AwesomeAssertions;
using Conduit.Runtime;

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
    public void SelfCausateForFirstDispatch()
    {
        // Act
        var context = MessageContext.StartDispatch();

        // Assert — a head/origin message self-causates: CausationId == MessageId == ConversationId
        context.ConversationId.Should().Be(context.MessageId);
        context.CausationId.Should().Be(context.MessageId);
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
    public void ReadAPropertyRegardlessOfCaseWhoeverBuiltTheBag()
    {
        // Arrange: a transport that hands over an ordinal bag, and one key spelled twice.
        var ordinal = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tenantcode"] = "first",
            ["TenantCode"] = "acme",
            ["UserId"] = "emp-1",
        };

        // Act
        var context = MessageContext.StartDispatchFromTransport("msg-1", null, "msg-1", ordinal);
        var child = MessageContext.StartDispatch(new DispatchOptions().Tag("Region", "eu"));

        // Assert: one property per name, the last spelling's value, found in any case; a local dispatch's tags too.
        context.Properties.Should().HaveCount(2);
        context.Properties["TENANTCODE"].Should().Be("acme");
        context.Properties["userid"].Should().Be("emp-1");
        child.Properties["region"].Should().Be("eu");
    }

    [TestMethod]
    public void DefaultTransportCausationToMessageIdWhenWireOmitsIt()
    {
        // Act — a producer that omitted the causation header (non-Conduit sender)
        var context = MessageContext.StartDispatchFromTransport(
            messageId: "msg-123",
            causationId: null,
            conversationId: "conv-789");

        // Assert — self-causation default keeps CausationId non-null
        context.MessageId.Should().Be("msg-123");
        context.CausationId.Should().Be("msg-123");
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

    [TestMethod]
    public void DeliverItsOwnMessageOnADispatch()
    {
        // Act
        var parent = MessageContext.StartDispatch();
        var child = MessageContext.StartDispatch();

        // Assert — a nested dispatch is a message of its own, so it does not inherit the parent's delivered id
        parent.DeliveredMessageId.Should().Be(parent.MessageId);
        child.DeliveredMessageId.Should().Be(child.MessageId);
    }

    [TestMethod]
    public void DeliverTheWireMessageOnATransportDispatch()
    {
        // Act
        var context = MessageContext.StartDispatchFromTransport("msg-123", "cause-456", "conv-789");

        // Assert — a broker redelivery carries the same MessageId, so it is the same delivered message
        context.DeliveredMessageId.Should().Be("msg-123");
    }

    [TestMethod]
    public void DeliverTheCurrentMessageOnALocalDelivery()
    {
        // Arrange — the outbox restores the entry's context before delivering it
        MessageContext.StartDispatchFromTransport("entry-1", null, "conv-1");

        // Act
        var first = MessageContext.StartLocalDelivery();
        MessageContext.StartDispatchFromTransport("entry-1", null, "conv-1");
        var second = MessageContext.StartLocalDelivery();
        var nested = MessageContext.StartDispatch();

        // Assert — each attempt is a dispatch of its own, but both deliver the entry
        first.MessageId.Should().NotBe("entry-1").And.NotBe(second.MessageId);
        first.CausationId.Should().Be("entry-1");
        first.ConversationId.Should().Be("conv-1");
        first.DeliveredMessageId.Should().Be("entry-1");
        second.DeliveredMessageId.Should().Be("entry-1");
        nested.DeliveredMessageId.Should().Be(nested.MessageId, "a listener's nested dispatch is a message of its own");
    }

    [TestMethod]
    public void DeliverItsOwnMessageOnALocalDeliveryWithNoCurrentContext()
    {
        // Act
        var context = MessageContext.StartLocalDelivery();

        // Assert
        context.DeliveredMessageId.Should().Be(context.MessageId);
        context.CausationId.Should().Be(context.MessageId);
    }
}
