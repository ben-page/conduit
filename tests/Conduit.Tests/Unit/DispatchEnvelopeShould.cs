using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

[TestClass]
public class DispatchEnvelopeShould
{
    [TestMethod]
    public void RejectNullType()
    {
        // Arrange
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act
        var action = () => new DispatchEnvelope(null!, payload);

        // Assert
        action.Should().ThrowExactly<ArgumentNullException>();
    }

    [TestMethod]
    public void RejectWhitespaceType()
    {
        // Arrange
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act
        var action = () => new DispatchEnvelope("  ", payload);

        // Assert
        action.Should().ThrowExactly<ArgumentException>();
    }
}
