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

    [TestMethod]
    public async Task CarryTheDeclaredStatus()
    {
        var result = await DispatchThrowing(new ConflictException("no"));

        result.Error!.Status.Should().Be(409);
        result.Error.Type.Should().BeNull();
        result.Error.Data.Should().BeNull();
    }

    [TestMethod]
    public async Task CarryACustomCodesDeclaredStatus()
    {
        var result = await DispatchThrowing(new DetailedRefusal(new ErrorCode("Custom", 422), "x", []));

        result.Error!.Status.Should().Be(422);
    }

    [TestMethod]
    public async Task CarryASubclassesTypeAndData()
    {
        var result = await DispatchThrowing(new BlockedRefusal(3, ["Ada", "Grace"], "blocked"));

        result.Error!.Status.Should().Be(409);
        result.Error.Type.Should().Be(typeof(BlockedRefusal).FullName);
        result.Error.Data!.Value.GetProperty("total").GetInt32().Should().Be(3);
        result.Error.Data.Value.GetProperty("names")[1].GetString().Should().Be("Grace");
        result.Error.Data.Value.TryGetProperty("message", out _).Should().BeFalse();
        result.Error.Data.Value.TryGetProperty("stackTrace", out _).Should().BeFalse();
    }

    [TestMethod]
    public async Task HideEverythingForAForeignException()
    {
        var result = await DispatchThrowing(new InvalidOperationException("SELECT * FROM secrets"));

        result.Error!.Code.Should().Be("InternalError");
        result.Error.Status.Should().Be(500);
        result.Error.Message.Should().Be("An unexpected error occurred");
        result.Error.Type.Should().BeNull();
        result.Error.Data.Should().BeNull();
    }

    // Public (not the usual private-nested-exception pattern in this file) so ConduitExceptionShould
    // can assert ClientDataProperties against it too -- CA1034 flags that on principle, but a shared
    // test fixture referenced from another test class is exactly the exception the rule allows for.
#pragma warning disable CA1034
    public sealed class BlockedRefusal(int total, IReadOnlyList<string> names, string message)
        : ConflictException(message)
    {
        public int Total => total;

        public IReadOnlyList<string> Names => names;
    }
#pragma warning restore CA1034

    private sealed class DetailedRefusal(
        ErrorCode code, string message, IReadOnlyList<DispatchErrorDetail> details)
        : ConduitException(code, message, details);
}
