using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class WireExceptionTranslatorShould
{
    private sealed class StaleWrite : Exception;

    private sealed class StaleWriteTranslator : IWireExceptionTranslator
    {
        public ConduitException? Translate(Exception exception) =>
            exception is StaleWrite ? new PreconditionFailedException("Someone else changed this. Refresh and try again.") : null;
    }

    private sealed class ThrowingTranslator : IWireExceptionTranslator
    {
        public ConduitException? Translate(Exception exception) => throw new InvalidOperationException("translator bug");
    }

    private static Task<DispatchResult<object>> DispatchThrowing(Exception toThrow, params IWireExceptionTranslator[] translators) =>
        ConduitRuntime.HandleWireRequest(
            "Test.Stale",
            [],
            () => new DispatchEnvelope("Test.Stale", JsonSerializer.SerializeToElement(new { })),
            (_, _, _) => throw toThrow,
            NullLogger.Instance,
            translators,
            CancellationToken.None);

    [TestMethod]
    public async Task AnswerTheTranslatedRefusal()
    {
        var result = await DispatchThrowing(new StaleWrite(), new StaleWriteTranslator());

        result.Error!.Code.Should().Be("PreconditionFailed");
        result.Error.Reference.Should().BeNull("a translated refusal is not a bug, so it carries no support reference");
    }

    [TestMethod]
    public async Task StillAnswerInternalErrorWhenNoTranslatorClaimsIt()
    {
        var result = await DispatchThrowing(new InvalidOperationException("boom"), new StaleWriteTranslator());

        result.Error!.Code.Should().Be("InternalError");
        result.Error.Reference.Should().NotBeNull();
    }

    [TestMethod]
    public async Task FallThroughToInternalErrorWhenATranslatorThrows()
    {
        var result = await DispatchThrowing(new StaleWrite(), new ThrowingTranslator(), new StaleWriteTranslator());

        // The faulty translator is skipped and the next one still gets its turn.
        result.Error!.Code.Should().Be("PreconditionFailed");

        var onlyFaulty = await DispatchThrowing(new InvalidOperationException("boom"), new ThrowingTranslator());
        onlyFaulty.Error!.Code.Should().Be("InternalError");
        onlyFaulty.Error.Reference.Should().NotBeNull();
    }
}
