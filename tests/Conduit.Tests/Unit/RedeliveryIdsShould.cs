using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class RedeliveryIdsShould
{
    // A replay sends all three ids in native properties, where a blank one would reach the receiver as an id
    [TestMethod]
    [DataRow("", "cause", "conversation", "messageId")]
    [DataRow(" ", "cause", "conversation", "messageId")]
    [DataRow("message", "", "conversation", "causationId")]
    [DataRow("message", "cause", " ", "conversationId")]
    public void RequireEveryId(string messageId, string causationId, string conversationId, string parameterName)
    {
        // Act
        var act = () => new RedeliveryIds(messageId, causationId, conversationId);

        // Assert
        act.Should().Throw<ArgumentException>().WithParameterName(parameterName);
    }

    [TestMethod]
    public void KeepTheIdsItIsGiven()
    {
        // Act
        var ids = new RedeliveryIds("message", "cause", "conversation");

        // Assert
        ids.MessageId.Should().Be("message");
        ids.CausationId.Should().Be("cause");
        ids.ConversationId.Should().Be("conversation");
    }
}
