using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Tests.Unit;

/// <summary>
/// Pins the exception-to-wire translation in <see cref="ConduitRuntime.HandleWireRequest"/>: a
/// <see cref="ConduitException"/>'s operands must reach <c>DispatchResult.Error.Details</c>, because
/// that is the only path a SPA client can read them from.
/// </summary>
[TestClass]
public sealed class WireRequestFailureShould
{
    private static Task<DispatchResult<object>> DispatchThrowing(Exception toThrow) =>
        ConduitRuntime.HandleWireRequest(
            "Test.Refusal",
            [],
            () => new DispatchEnvelope("Test.Refusal", JsonSerializer.SerializeToElement(new { })),
            (_, _, _) => throw toThrow,
            NullLogger.Instance,
            CancellationToken.None);

    [TestMethod]
    public async Task ForwardTheExceptionsDetails()
    {
        // Arrange
        var ex = new DetailedRefusal(
            new ErrorCode("LockBlockedByEmployees", 400),
            "blocked",
            [new DispatchErrorDetail(null, "392"), new DispatchErrorDetail(null, "Ada Lovelace")]);

        // Act
        var result = await DispatchThrowing(ex);

        // Assert
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("LockBlockedByEmployees");
        result.Error.Message.Should().Be("blocked");
        result.Error.Details.Should().HaveCount(2);
        result.Error.Details![0].Message.Should().Be("392");
        result.Error.Details[1].Message.Should().Be("Ada Lovelace");
    }

    [TestMethod]
    public async Task LeaveDetailsNullWhenTheExceptionCarriesNone()
    {
        // Act -- the shape every pre-existing throw site produces; it must not change.
        var result = await DispatchThrowing(new BadRequestException("plain"));

        // Assert
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("BadRequest");
        result.Error.Details.Should().BeNull();
    }

    private sealed class DetailedRefusal(
        ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail> details)
        : ConduitException(code, message, details);
}
