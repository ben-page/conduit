using AwesomeAssertions;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitExceptionShould
{
    [TestMethod]
    public void HaveNotFoundCode()
    {
        var ex = new Exceptions.NotFoundException("not found");
        ex.Code.Should().Be("NotFound");
        ex.Message.Should().Be("not found");
    }

    [TestMethod]
    public void HaveUnauthorizedCode()
    {
        var ex = new Exceptions.UnauthorizedException("denied");
        ex.Code.Should().Be("Unauthorized");
    }

    [TestMethod]
    public void HaveConflictCode()
    {
        var ex = new Exceptions.ConflictException("conflict");
        ex.Code.Should().Be("Conflict");
    }

    [TestMethod]
    public void BeAssignableToException()
    {
        var ex = new Exceptions.NotFoundException("test");
        ex.Should().BeAssignableTo<Exception>();
    }
}
