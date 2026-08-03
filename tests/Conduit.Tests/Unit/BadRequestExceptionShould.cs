using AwesomeAssertions;
using Conduit.Exceptions;

namespace Conduit.Tests.Unit;

[TestClass]
public class BadRequestExceptionShould
{
    [TestMethod]
    public void CarriesTheBadRequestCodeAndMessage()
    {
        // Arrange & Act
        var ex = new BadRequestException("Name is required.");

        // Assert
        ex.Code.Should().Be(ErrorCode.BadRequest);
        ex.Code.HttpStatusCode.Should().Be(400);
        ex.Message.Should().Be("Name is required.");
        ex.Should().BeAssignableTo<ConduitException>();
    }
}
