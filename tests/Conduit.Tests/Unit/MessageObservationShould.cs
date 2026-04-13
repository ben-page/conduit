using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class MessageObservationShould
{
    [TestMethod]
    public void CreateDispatchInvokedWithAutoTimestamp()
    {
        // Arrange & Act
        var observation = new DispatchInvoked("Test.Commands.DoThing", "Command");

        // Assert
        observation.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        observation.MessageType.Should().Be("Test.Commands.DoThing");
        observation.MessageKind.Should().Be("Command");
    }

    [TestMethod]
    public void CreateDispatchCompletedWithDuration()
    {
        // Arrange & Act
        var observation = new DispatchCompleted(142.5);

        // Assert
        observation.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(1));
        observation.DurationMs.Should().Be(142.5);
    }

    [TestMethod]
    public void CreateDispatchFailedWithErrorDetails()
    {
        // Arrange & Act
        var observation = new DispatchFailed(55.0, "null reference", "NullReferenceException");

        // Assert
        observation.ErrorMessage.Should().Be("null reference");
        observation.ExceptionType.Should().Be("NullReferenceException");
        observation.DurationMs.Should().Be(55.0);
    }

    [TestMethod]
    public void CreateRetryAttemptedWithAllFields()
    {
        // Arrange & Act
        var observation = new RetryAttempted(2, "timeout", "TimeoutException", 800.0);

        // Assert
        observation.Attempt.Should().Be(2);
        observation.ErrorMessage.Should().Be("timeout");
        observation.ExceptionType.Should().Be("TimeoutException");
        observation.DelayMs.Should().Be(800.0);
    }

    [TestMethod]
    public void CreateCustomObservationWithData()
    {
        // Arrange
        var data = new { attempt = 1, status = 503 };

        // Act
        var observation = new CustomObservation("as2.attempt", data);

        // Assert
        observation.Name.Should().Be("as2.attempt");
        observation.Data.Should().NotBeNull();
    }

    [TestMethod]
    public void InheritFromMessageObservationBaseType()
    {
        // Arrange & Act
        MessageObservation observation = new HandlerStarted("MyHandler");

        // Assert
        observation.Should().BeOfType<HandlerStarted>();
    }
}
