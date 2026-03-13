using AwesomeAssertions;

namespace Conduit.Tests;

[TestClass]
public class ExceptionTests
{
    [TestMethod]
    public void NotFoundException_HasCorrectCode()
    {
        var ex = new Conduit.Exceptions.NotFoundException("not found");
        ex.Code.Should().Be("NotFound");
        ex.Message.Should().Be("not found");
    }

    [TestMethod]
    public void UnauthorizedException_HasCorrectCode()
    {
        var ex = new Conduit.Exceptions.UnauthorizedException("denied");
        ex.Code.Should().Be("Unauthorized");
    }

    [TestMethod]
    public void ConflictException_HasCorrectCode()
    {
        var ex = new Conduit.Exceptions.ConflictException("conflict");
        ex.Code.Should().Be("Conflict");
    }

    [TestMethod]
    public void ConduitException_IsException()
    {
        var ex = new Conduit.Exceptions.NotFoundException("test");
        ex.Should().BeAssignableTo<Exception>();
    }
}
