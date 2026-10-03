using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Tests.Unit;

/// <summary>
/// A wire payload the contract cannot read is the CALLER's fault: it answers 400, never InternalError. And the
/// contract spells an enum by NAME -- an integer ordinal, in range or not, is refused, so an out-of-range value can
/// never reach a foreign-key column as a 500 (DMS open:conduit-enum-ordinals-reach-lookup-fks).
/// </summary>
[TestClass]
public sealed class WirePayloadShould
{
#pragma warning disable CA1034 // test fixtures
    public enum Color { Invalid = 0, Red = 1, Blue = 2 }

    public sealed record Probe(Color Color, Color? Maybe);

    public sealed record Contract(int Id, string Title, string? Notes, DateOnly? Due, int Take = 50);
#pragma warning restore CA1034

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void ReadAnEnumByName()
    {
        var probe = ConduitRuntime.DeserializeWire<Probe>(Json("""{"color":"Blue","maybe":null}"""));

        probe.Color.Should().Be(Color.Blue);
        probe.Maybe.Should().BeNull();
    }

    [TestMethod]
    public void RefuseAnOutOfRangeOrdinal()
    {
        var act = () => ConduitRuntime.DeserializeWire<Probe>(Json("""{"color":999}"""));

        act.Should().Throw<BadRequestException>().Which.Message.Should().Contain("$.color");
    }

    [TestMethod]
    public void RefuseAnOrdinalThatNamesARealMember()
    {
        var act = () => ConduitRuntime.DeserializeWire<Probe>(Json("""{"color":1}"""));

        act.Should().Throw<BadRequestException>();
    }

    [TestMethod]
    public void RefuseAnOrdinalInANullableEnum()
    {
        var act = () => ConduitRuntime.DeserializeWire<Probe>(Json("""{"color":"Red","maybe":2}"""));

        act.Should().Throw<BadRequestException>().Which.Message.Should().Contain("$.maybe");
    }

    [TestMethod]
    public void RefuseANullPayload()
    {
        // "null" deserializes to null, which would reach the handler as an NRE and answer InternalError.
        var act = () => ConduitRuntime.DeserializeWire<Probe>(Json("null"));

        act.Should().Throw<BadRequestException>().Which.Message.Should().Be("The request could not be read.");
    }

    [TestMethod]
    public void StillWriteEnumsByName()
    {
        JsonSerializer.Serialize(Color.Red, ConduitRuntime.JsonOptions).Should().Be("\"Red\"");
    }

    [TestMethod]
    public async Task AnswerBadRequestThroughTheWireEntryPoint()
    {
        var result = await ConduitRuntime.HandleWireRequest(
            "Test.Probe",
            [],
            () => new DispatchEnvelope("Test.Probe", Json("""{"color":999}""")),
            (_, payload, _) => Task.FromResult<object?>(ConduitRuntime.DeserializeWire<Probe>(payload)),
            NullLogger.Instance,
            CancellationToken.None);

        result.Error!.Code.Should().Be("BadRequest");
        result.Error.Status.Should().Be(400);
    }

    [TestMethod]
    public async Task ASerializerFailureInsideTheHandler_IsStillInternalError()
    {
        // Only the payload READ is the caller's fault. A handler that trips System.Text.Json on its own data is a bug.
        var result = await ConduitRuntime.HandleWireRequest(
            "Test.Probe",
            [],
            () => new DispatchEnvelope("Test.Probe", Json("""{"color":"Red","maybe":null}""")),
            (_, payload, _) =>
            {
                _ = ConduitRuntime.DeserializeWire<Probe>(payload);
                return Task.FromResult<object?>(JsonSerializer.Deserialize<Probe>("not json"));
            },
            NullLogger.Instance,
            CancellationToken.None);

        result.Error!.Code.Should().Be("InternalError");
        result.Error.Status.Should().Be(500);
    }

    [TestMethod]
    public void RefuseAPayloadMissingANullableMember()
    {
        var act = () => ConduitRuntime.DeserializeWire<Contract>(Json("""{"id":1,"title":"t","due":null}"""));

        var thrown = act.Should().Throw<ContractMismatchException>().Which;
        thrown.Details!.Select(d => d.Field).Should().Equal("notes");
        thrown.Details![0].Message.Should().Be("missing");
        thrown.Code.Value.Should().Be("ContractMismatch");
        thrown.Code.HttpStatusCode.Should().Be(400);
        thrown.Message.Should().Be("This page is out of date: the request is missing notes. Reload to continue.");
    }

    [TestMethod]
    public void NameEveryMissingMemberInConstructorOrder()
    {
        var act = () => ConduitRuntime.DeserializeWire<Contract>(Json("""{"title":"t"}"""));

        var thrown = act.Should().Throw<ContractMismatchException>().Which;
        thrown.Details!.Select(d => d.Field).Should().Equal("id", "notes", "due");
        thrown.Message.Should().Contain("id, notes, due");
    }

    [TestMethod]
    public void AcceptAnExplicitNull() =>
        ConduitRuntime.DeserializeWire<Contract>(Json("""{"id":1,"title":"t","notes":null,"due":null}""")).Notes.Should().BeNull();

    [TestMethod]
    public void AcceptAnOmittedDefaultedMember() =>
        ConduitRuntime.DeserializeWire<Contract>(Json("""{"id":1,"title":"t","notes":null,"due":null}""")).Take.Should().Be(50);

    [TestMethod]
    public void RefuseAPayloadMissingANonNullableValue()
    {
        var act = () => ConduitRuntime.DeserializeWire<Contract>(Json("""{"title":"t","notes":null,"due":null}"""));

        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("id");
    }

    [TestMethod]
    public void KeepAWrongTypedFieldABadRequestEvenWhenAMemberIsAlsoMissing()
    {
        var act = () => ConduitRuntime.DeserializeWire<Contract>(Json("""{"id":"x","title":"t"}"""));

        act.Should().Throw<BadRequestException>().Which.Should().NotBeOfType<ContractMismatchException>();
    }

    [TestMethod]
    public void LeaveTheSharedOptionsLenient()
    {
        JsonSerializer.Deserialize<Contract>("""{"id":1,"title":"t"}""", ConduitRuntime.JsonOptions)!
            .Notes.Should().BeNull();
        ConduitRuntime.JsonOptions.RespectRequiredConstructorParameters.Should().BeFalse();
    }

    [TestMethod]
    public async Task AnswerContractMismatchThroughTheWireEntryPoint()
    {
        var result = await ConduitRuntime.HandleWireRequest(
            "Test.Contract",
            [],
            () => new DispatchEnvelope("Test.Contract", Json("""{"id":1,"title":"t","due":null}""")),
            (_, payload, _) => Task.FromResult<object?>(ConduitRuntime.DeserializeWire<Contract>(payload)),
            NullLogger.Instance,
            CancellationToken.None);

        result.Error!.Code.Should().Be("ContractMismatch");
        result.Error.Status.Should().Be(400);
        result.Error.Details!.Select(d => d.Field).Should().Equal("notes");
    }
}
