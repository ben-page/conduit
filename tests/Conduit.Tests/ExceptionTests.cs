using AwesomeAssertions;

namespace Conduit.Tests;

[TestClass]
public class ExceptionTests
{
    [TestMethod]
    public void ValidationException_HasCorrectCodeAndStatus()
    {
        var ex = new Conduit.Exceptions.ValidationException("bad input");
        ex.Code.Should().Be("Validation");
        ex.StatusCode.Should().Be(422);
        ex.Message.Should().Be("bad input");
    }

    [TestMethod]
    public void NotFoundException_HasCorrectCodeAndStatus()
    {
        var ex = new Conduit.Exceptions.NotFoundException("not found");
        ex.Code.Should().Be("NotFound");
        ex.StatusCode.Should().Be(404);
    }

    [TestMethod]
    public void UnauthorizedException_HasCorrectCodeAndStatus()
    {
        var ex = new Conduit.Exceptions.UnauthorizedException("denied");
        ex.Code.Should().Be("Unauthorized");
        ex.StatusCode.Should().Be(401);
    }

    [TestMethod]
    public void ConflictException_HasCorrectCodeAndStatus()
    {
        var ex = new Conduit.Exceptions.ConflictException("conflict");
        ex.Code.Should().Be("Conflict");
        ex.StatusCode.Should().Be(409);
    }

    [TestMethod]
    public void ConduitException_IsException()
    {
        var ex = new Conduit.Exceptions.NotFoundException("test");
        ex.Should().BeAssignableTo<Exception>();
    }
}
