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
public sealed partial class WireRequestFailureShould
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
    public async Task CarryADatalessConsumerRefusalsTypeAndEmptyData()
    {
        var result = await DispatchThrowing(new DatalessRefusal());

        result.Error!.Type.Should().Be(typeof(DatalessRefusal).FullName);
        result.Error.Data.Should().NotBeNull();
        result.Error.Data!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        result.Error.Data.Value.EnumerateObject().Should().BeEmpty();
    }

    [TestMethod]
    public async Task CarryEmptyDataWhenATypedRefusalsOverrideReturnsNull()
    {
        // A typed refusal whose GetClientData override returns null must still send {} on the wire --
        // the client narrows on Type alone and always expects an object, never a missing Data.
        var result = await DispatchThrowing(new OverriddenNullDataRefusal());

        result.Error!.Type.Should().Be(typeof(OverriddenNullDataRefusal).FullName);
        result.Error.Data.Should().NotBeNull();
        result.Error.Data!.Value.ValueKind.Should().Be(JsonValueKind.Object);
        result.Error.Data.Value.EnumerateObject().Should().BeEmpty();
    }

    [TestMethod]
    public async Task HideEverythingForAForeignException()
    {
        var result = await DispatchThrowing(new InvalidOperationException("SELECT * FROM secrets"));

        result.Error!.Code.Should().Be("InternalError");
        result.Error.Status.Should().Be(500);
        result.Error.Message.Should().Be("An unexpected error occurred.");
        result.Error.Reference.Should().MatchRegex("^[A-Za-z0-9-]{1,64}$");
        result.Error.Message.Should().NotContain("secrets");
        result.Error.Type.Should().BeNull();
        result.Error.Data.Should().BeNull();
    }

    [TestMethod]
    public async Task LogTheSameReferenceItAnswersWith()
    {
        // Support gets the reference from the user and greps for it -- DMS logs without scopes, so the id must be IN the line.
        var logger = new CapturingLogger();
        var result = await ConduitRuntime.HandleWireRequest(
            "Test.Refusal",
            [],
            () => new DispatchEnvelope("Test.Refusal", JsonSerializer.SerializeToElement(new { })),
            (_, _, _) => throw new InvalidOperationException("boom"),
            logger,
            CancellationToken.None);

        var reference = result.Error!.Reference;
        reference.Should().NotBeNullOrEmpty();
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error)
            .Which.Message.Should().Contain(reference);
    }

    [TestMethod]
    public async Task MintAReferenceWhenTheConversationIdIsNotLogSafe()
    {
        // The hub accepts a caller-supplied context, so the ConversationId is attacker-controlled: CR/LF would forge log
        // lines. Anything but [A-Za-z0-9-]{1,64} is replaced.
        const string injected = "abc\r\n2026-01-01 FAKE (reference x)";
        MessageContext.StartDispatchFromTransport("m-1", null, injected);
        try
        {
            var logger = new CapturingLogger();
            var result = await ConduitRuntime.HandleWireRequest(
                "Test.Refusal",
                [],
                () => new DispatchEnvelope("Test.Refusal", JsonSerializer.SerializeToElement(new { })),
                (_, _, _) => throw new InvalidOperationException("boom"),
                logger,
                CancellationToken.None);

            result.Error!.Message.Should().Be("An unexpected error occurred.");
            result.Error.Reference.Should().MatchRegex("^[0-9a-f]{32}$");
            result.Error.Message.Should().NotContain("FAKE").And.NotContain("abc");
            var reference = result.Error.Reference!;
            logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error)
                .Which.Message.Should().Contain(reference).And.NotContain("FAKE");
        }
        finally
        {
            MessageContext.RestoreContext(null);
        }
    }

    [TestMethod]
    public async Task KeepALogSafeConversationIdAsTheReference()
    {
        MessageContext.StartDispatchFromTransport("m-1", null, "conv-123-ABC");
        try
        {
            var result = await DispatchThrowing(new InvalidOperationException("boom"));

            result.Error!.Message.Should().Be("An unexpected error occurred.");
            result.Error.Reference.Should().Be("conv-123-ABC");
        }
        finally
        {
            MessageContext.RestoreContext(null);
        }
    }

    [TestMethod]
    public async Task CarryNoReferenceOnARefusal()
    {
        var result = await DispatchThrowing(new NotFoundException("gone"));

        result.Error!.Reference.Should().BeNull();
    }

    [TestMethod]
    public void KeepAReferenceTheExceptionCarries()
    {
        var result = DispatchResult<object>.Fail(new DatalessRefusal { Reference = "abc" });

        result.Error!.Reference.Should().Be("abc");
    }

    [TestMethod]
    public void RoundTripTheReferenceThroughJsonAndTolerateItsAbsence()
    {
        var failed = DispatchResult<object>.Fail(new DatalessRefusal { Reference = "abc" });
        var json = JsonSerializer.Serialize(failed, ConduitRuntime.JsonOptions);
        json.Should().Contain("\"reference\":\"abc\"");
        JsonSerializer.Deserialize<DispatchResult<object>>(json, ConduitRuntime.JsonOptions)!.Error!.Reference.Should().Be("abc");

        // An older server sends no reference.
        var old = JsonSerializer.Deserialize<DispatchResult<object>>(
            "{\"error\":{\"code\":\"InternalError\",\"message\":\"An unexpected error occurred.\",\"status\":500}}",
            ConduitRuntime.JsonOptions);
        old!.Error!.Reference.Should().BeNull();
    }

    [TestMethod]
    public void PutTheReferenceOnTheExceptionUnwrapReplyThrows()
    {
        var reply = JsonSerializer.Deserialize<DispatchResult<JsonElement?>>(
            "{\"error\":{\"code\":\"InternalError\",\"message\":\"An unexpected error occurred.\",\"status\":500,\"reference\":\"ref-1\"}}",
            ConduitRuntime.JsonOptions);
        var act = () => ConduitRuntime.UnwrapReply(reply);

        var thrown = act.Should().Throw<RemoteDispatchException>().Which;
        thrown.Reference.Should().Be("ref-1");
        thrown.Message.Should().Contain("ref-1").And.Contain("InternalError");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => this.Entries.Add((logLevel, formatter(state, exception)));
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

    private sealed class DatalessRefusal() : ConflictException("refused");

    private sealed class OverriddenNullDataRefusal() : ConflictException("refused")
    {
        public override object? GetClientData() => null;
    }
}
