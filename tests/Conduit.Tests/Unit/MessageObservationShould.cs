using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class MessageObservationShould
{
    [TestMethod]
    public void CreateDispatchInvokedWithTimestamp()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var observation = new DispatchInvoked(timestamp);

        // Assert
        observation.Timestamp.Should().Be(timestamp);
    }

    [TestMethod]
    public void CreateDispatchCompletedWithDuration()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var observation = new DispatchCompleted(timestamp, 142.5);

        // Assert
        observation.Timestamp.Should().Be(timestamp);
        observation.DurationMs.Should().Be(142.5);
    }

    [TestMethod]
    public void CreateDispatchFailedWithErrorDetails()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var observation = new DispatchFailed(timestamp, 55.0, "null reference", "NullReferenceException");

        // Assert
        observation.ErrorMessage.Should().Be("null reference");
        observation.ExceptionType.Should().Be("NullReferenceException");
        observation.DurationMs.Should().Be(55.0);
    }

    [TestMethod]
    public void CreateRetryAttemptedWithAllFields()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        var observation = new RetryAttempted(timestamp, 2, "timeout", "TimeoutException", 800.0);

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
        var timestamp = DateTimeOffset.UtcNow;
        var data = new { attempt = 1, status = 503 };

        // Act
        var observation = new CustomObservation(timestamp, "as2.attempt", data);

        // Assert
        observation.Name.Should().Be("as2.attempt");
        observation.Data.Should().NotBeNull();
    }

    [TestMethod]
    public void InheritFromMessageObservationBaseType()
    {
        // Arrange & Act
        MessageObservation observation = new HandlerStarted(DateTimeOffset.UtcNow, "MyHandler");

        // Assert
        observation.Should().BeOfType<HandlerStarted>();
    }
}
