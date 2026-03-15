using AwesomeAssertions;
using Conduit.Exceptions;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitExceptionShould
{
    [TestMethod]
    public void HaveNotFoundCode()
    {
        // Arrange & Act
        var ex = new NotFoundException("not found");

        // Assert
        ex.Code.Should().Be(ErrorCode.NotFound);
        ex.Message.Should().Be("not found");
    }

    [TestMethod]
    public void HaveUnauthorizedCode()
    {
        // Arrange & Act
        var ex = new UnauthorizedException("denied");

        // Assert
        ex.Code.Should().Be(ErrorCode.Unauthorized);
    }

    [TestMethod]
    public void HaveConflictCode()
    {
        // Arrange & Act
        var ex = new ConflictException("conflict");

        // Assert
        ex.Code.Should().Be(ErrorCode.Conflict);
    }

    [TestMethod]
    public void BeAssignableToException()
    {
        // Arrange & Act
        var ex = new NotFoundException("test");

        // Assert
        ex.Should().BeAssignableTo<Exception>();
    }
}
