using AwesomeAssertions;
using Conduit.Exceptions;

namespace Conduit.Tests.Unit;

[TestClass]
public class EventErrorReportingShould
{
    [TestMethod]
    public void CarryListenerExceptions()
    {
        // Arrange
        var inner1 = new InvalidOperationException("listener 1 failed");
        var inner2 = new ArgumentException("listener 2 failed");

        // Act
        var exception = new EventListenerException(
            "One or more listeners failed",
            [inner1, inner2]);

        // Assert
        exception.ListenerExceptions.Should().HaveCount(2);
        exception.ListenerExceptions[0].Should().BeSameAs(inner1);
        exception.ListenerExceptions[1].Should().BeSameAs(inner2);
        exception.Message.Should().Be("One or more listeners failed");
    }

    [TestMethod]
    public void ChainTheFirstListenerExceptionAsInnerException()
    {
        // Arrange
        var first = new InvalidOperationException("listener 1 failed");
        var second = new ArgumentException("listener 2 failed");

        // Act
        var exception = new EventListenerException("One or more listeners failed", [first, second]);

        // Assert
        exception.InnerException.Should().BeSameAs(first);
    }

    [TestMethod]
    public void RequireTheListenerExceptions()
    {
        // Arrange
        var create = () => new EventListenerException("One or more listeners failed", null!);

        // Act / Assert
        create.Should().ThrowExactly<ArgumentNullException>().WithParameterName("listenerExceptions");
    }

    [TestMethod]
    public void HaveNoInnerExceptionWhenNoListenerFailed()
    {
        // Arrange / Act
        var exception = new EventListenerException("One or more listeners failed", []);

        // Assert
        exception.InnerException.Should().BeNull();
    }
}
