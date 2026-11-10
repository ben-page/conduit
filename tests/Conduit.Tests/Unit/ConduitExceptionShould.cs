using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;

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
    public void HavePreconditionFailedCode()
    {
        // Arrange & Act
        var ex = new PreconditionFailedException("stale");

        // Assert
        ex.Code.Should().Be(ErrorCode.PreconditionFailed);
        ex.Message.Should().Be("stale");
    }

    [TestMethod]
    public void BeAssignableToException()
    {
        // Arrange & Act
        var ex = new NotFoundException("test");

        // Assert
        ex.Should().BeAssignableTo<Exception>();
    }

    [TestMethod]
    public void CarryNullDetailsByDefault()
    {
        // Arrange & Act
        var ex = new BadRequestException("no operands");

        // Assert -- every existing subclass and throw site keeps compiling and keeps meaning null.
        ex.Details.Should().BeNull();
    }

    [TestMethod]
    public void CarryTheDetailsItWasConstructedWith()
    {
        // Arrange & Act
        var ex = new DetailedTestException(
            new ErrorCode("ShiftOverlaps", 400),
            "refused",
            [new DispatchErrorDetail(null, "16:10")]);

        // Assert
        ex.Code.Value.Should().Be("ShiftOverlaps");
        ex.Code.HttpStatusCode.Should().Be(400);
        ex.Details.Should().ContainSingle().Which.Should().Be(new DispatchErrorDetail(null, "16:10"));
    }

    [TestMethod]
    public void ExposeNoClientDataForAPlainStatusException()
    {
        var ex = new ConflictException("x");

        ex.GetClientData().Should().BeNull();
        ex.GetClientType().Should().BeNull();
    }

    [TestMethod]
    public void ExposeOnlyPropertiesDeclaredBelowConduitException()
    {
        ConduitException.ClientDataProperties(typeof(WireRequestFailureShould.BlockedRefusal))
            .Select(p => p.Name).Should().BeEquivalentTo(["Total", "Names"]);
    }

    [TestMethod]
    public void TypeAConsumerSubclassThatDeclaresNoData()
    {
        var ex = new DatalessConsumerRefusal();

        ex.GetClientType().Should().Be(typeof(DatalessConsumerRefusal).FullName);
        ex.GetClientData().Should().BeAssignableTo<IDictionary<string, object?>>()
            .Which.Should().BeEmpty("a typed refusal's data is an empty object, never null");
    }

    [TestMethod]
    public void RequireACode()
    {
        // Arrange -- without the guard, the missing code surfaced as a NullReferenceException thrown from inside the
        // runtime's catch (ConduitException) arm, where the wire reads Code
        var create = () => new DetailedTestException(null!, "refused", []);

        // Act / Assert
        create.Should().ThrowExactly<ArgumentNullException>().WithParameterName("code");
    }

    [TestMethod]
    public void LeaveTheBuiltInStatusExceptionsUntyped()
    {
        new ConflictException("x").GetClientType().Should().BeNull();
        new ConflictException("x").GetClientData().Should().BeNull();
        ConduitException.IsClientTyped(typeof(ConflictException)).Should().BeFalse();
    }

    [TestMethod]
    public void TypeAConsumerSubclassWithData_AsBefore() =>
        ConduitException.IsClientTyped(typeof(WireRequestFailureShould.BlockedRefusal)).Should().BeTrue();

    private sealed class DatalessConsumerRefusal() : ConflictException("refused");

    private sealed class DetailedTestException(
        ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail> details)
        : ConduitException(code, message, details);
}
