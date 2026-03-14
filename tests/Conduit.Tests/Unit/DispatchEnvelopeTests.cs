using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;

namespace Conduit.Tests.Unit;

[TestClass]
public class DispatchEnvelopeTests
{
    [TestMethod]
    public void Constructor_NullType_ThrowsArgumentNullException()
    {
        // Arrange
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act
        var action = () => new DispatchEnvelope(null!, payload);

        // Assert
        action.Should().ThrowExactly<ArgumentNullException>();
    }

    [TestMethod]
    public void Constructor_WhitespaceType_ThrowsArgumentException()
    {
        // Arrange
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act
        var action = () => new DispatchEnvelope("  ", payload);

        // Assert
        action.Should().ThrowExactly<ArgumentException>();
    }
}
