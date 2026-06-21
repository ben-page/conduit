using AwesomeAssertions;
using Conduit.Exceptions;

namespace Conduit.Tests.Unit;

[TestClass]
public class ErrorCodeShould
{
    [TestMethod]
    public void HaveCorrectValueAndStatusForBuiltInCodes()
    {
        // Arrange & Act & Assert
        ErrorCode.NotFound.Value.Should().Be("NotFound");
        ErrorCode.NotFound.HttpStatusCode.Should().Be(404);

        ErrorCode.Unauthorized.Value.Should().Be("Unauthorized");
        ErrorCode.Unauthorized.HttpStatusCode.Should().Be(401);

        ErrorCode.Forbidden.Value.Should().Be("Forbidden");
        ErrorCode.Forbidden.HttpStatusCode.Should().Be(403);

        ErrorCode.Conflict.Value.Should().Be("Conflict");
        ErrorCode.Conflict.HttpStatusCode.Should().Be(409);

        ErrorCode.InternalError.Value.Should().Be("InternalError");
        ErrorCode.InternalError.HttpStatusCode.Should().Be(500);

        ErrorCode.BadRequest.Value.Should().Be("BadRequest");
        ErrorCode.BadRequest.HttpStatusCode.Should().Be(400);
    }

    [TestMethod]
    public void ReturnValueFromToString()
    {
        // Arrange & Act & Assert
        ErrorCode.NotFound.ToString().Should().Be("NotFound");
    }

    [TestMethod]
    public void SupportConsumerDefinedCodes()
    {
        // Arrange
        var custom = new ErrorCode("InsufficientCredit", 422);

        // Act & Assert
        custom.Value.Should().Be("InsufficientCredit");
        custom.HttpStatusCode.Should().Be(422);
    }

    [TestMethod]
    public void UseValueOnlyForEquality()
    {
        // Arrange
        var a = new ErrorCode("NotFound", 404);
        var b = new ErrorCode("NotFound", 999);

        // Act & Assert
        a.Should().Be(b);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [TestMethod]
    public void NotEqualWhenValuesDiffer()
    {
        // Arrange & Act & Assert
        ErrorCode.NotFound.Should().NotBe(ErrorCode.Conflict);
    }
}
