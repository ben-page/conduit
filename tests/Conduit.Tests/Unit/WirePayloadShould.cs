using System.Text.Json;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Tests.Unit;

/// <summary>
/// A wire payload the contract cannot read is the CALLER's fault: it answers 400, never InternalError. And the
/// contract spells an enum by NAME -- an integer ordinal, in range or not, is refused, so an out-of-range value can
/// never reach a foreign-key column (an enum mirrored as a lookup table) as a 500.
/// </summary>
[TestClass]
public sealed class WirePayloadShould
{
#pragma warning disable CA1034 // test fixtures
    public enum Color { Invalid = 0, Red = 1, Blue = 2 }

    public sealed record Probe(Color Color, Color? Maybe);

    public sealed record Contract(int Id, string Title, string? Notes, DateOnly? Due, int Take = 50);

    public sealed record Column(string Name, int? WipLimit, [property: System.Text.Json.Serialization.JsonPropertyName("done")] bool IsDone);

    public sealed record Board(string Title, Column Lead, IReadOnlyList<Column> Columns);

    public sealed record Settable
    {
        public string Name { get; init; } = "";
        public int? WipLimit { get; init; }
    }

    public sealed record Wrapper(Settable Inner, IReadOnlyList<Contract> Contracts);

    public sealed record Throwing(string Name)
    {
        public string Name { get; } = Name ?? throw new ArgumentNullException(nameof(Name));
    }

    public sealed class Hidden
    {
#pragma warning disable IDE0051 // bound by System.Text.Json through [JsonConstructor]
        [System.Text.Json.Serialization.JsonConstructor]
        private Hidden(int secret) => this.Secret = secret;
#pragma warning restore IDE0051

        public Hidden(string other) => this.Other = other;

        public int Secret { get; }
        public string? Other { get; }
    }
    public sealed record Registry(IReadOnlyDictionary<string, Column> ByKey);

    public sealed record Tags(IReadOnlyList<int> Ids, IReadOnlyList<string> Names);

    public readonly record struct Tier(int Months, decimal? Cap);

    public sealed record TierPolicy(IReadOnlyList<Tier> Tiers);

    public sealed record NamedTierPolicy(string Name, IReadOnlyList<Tier> Tiers);

#pragma warning disable CA1815 // a wire fixture, never compared
    public readonly struct MarkedTier
    {
        [System.Text.Json.Serialization.JsonConstructor]
        public MarkedTier(int months, decimal? cap)
        {
            this.Months = months;
            this.Cap = cap;
        }

        public int Months { get; }

        public decimal? Cap { get; }
    }
#pragma warning restore CA1815

    public sealed record MarkedPolicy(IReadOnlyList<MarkedTier> Tiers);

    public sealed record Many(IReadOnlyList<Column> Columns);
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

    [TestMethod]
    public void NameAMissingMemberOfANestedPositionalModelByItsPath()
    {
        var act = () => ConduitRuntime.DeserializeWire<Board>(Json(
            """{"title":"b","lead":{"name":"l","done":false},"columns":[{"name":"a","wipLimit":1,"done":true},{"name":"b"}]}"""));

        var thrown = act.Should().Throw<ContractMismatchException>().Which;
        thrown.Details!.Select(d => d.Field).Should().Equal("lead.wipLimit", "columns[1].wipLimit", "columns[1].done");
        thrown.Code.Value.Should().Be("ContractMismatch");
    }

    [TestMethod]
    public void NameATopLevelMemberWhenTheNestedModelsAreComplete()
    {
        var act = () => ConduitRuntime.DeserializeWire<Board>(Json(
            """{"lead":{"name":"l","wipLimit":null,"done":false},"columns":[]}"""));

        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("title");
    }

    [TestMethod]
    public void NameATopLevelAndANestedMissingMemberTogether()
    {
        var act = () => ConduitRuntime.DeserializeWire<Board>(Json("""{"lead":{"name":"l","done":false},"columns":[]}"""));

        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("title", "lead.wipLimit");
    }

    [TestMethod]
    public void NameADictionaryValuesMissingMemberByTheContractPathNeverByTheClientsKey()
    {
        var act = () => ConduitRuntime.DeserializeWire<Registry>(Json(
            """{"byKey":{"a\r\nFAKE (reference x)":{"name":"a","done":true},"b":{"name":"b","done":false}}}"""));

        var thrown = act.Should().Throw<ContractMismatchException>().Which;
        thrown.Details!.Select(d => d.Field).Should().Equal("byKey[*].wipLimit");
        thrown.Message.Should().NotContain("FAKE");
    }

    [TestMethod]
    public void AcceptAnExplicitNullForANestedModelAndStopTheWalkThere() =>
        ConduitRuntime.DeserializeWire<Board>(Json("""{"title":"b","lead":null,"columns":[]}""")).Lead.Should().BeNull();

    [TestMethod]
    public void ReadArraysOfPrimitivesAndNameAMissingArray()
    {
        ConduitRuntime.DeserializeWire<Tags>(Json("""{"ids":[1,2],"names":["a"]}""")).Ids.Should().Equal(1, 2);

        var act = () => ConduitRuntime.DeserializeWire<Tags>(Json("""{"ids":[1]}"""));
        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("names");
    }

    [TestMethod]
    public void LeaveAStructWithoutJsonConstructorUnchecked()
    {
        // The documented bound: System.Text.Json builds such a struct through its default constructor and setters, so
        // nothing is required -- and the naming must not claim otherwise.
        ConduitRuntime.DeserializeWire<TierPolicy>(Json("""{"tiers":[{"months":1}]}""")).Tiers[0].Cap.Should().BeNull();

        // When a class member IS missing, the struct's members are not named as if they were required.
        var act = () => ConduitRuntime.DeserializeWire<NamedTierPolicy>(Json("""{"tiers":[{"months":1}]}"""));
        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("name");
    }

    [TestMethod]
    public void NameAMissingMemberOfAStructThatBindsAJsonConstructor()
    {
        var act = () => ConduitRuntime.DeserializeWire<MarkedPolicy>(Json("""{"tiers":[{"months":1}]}"""));

        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("tiers[0].cap");
    }

    [TestMethod]
    public void NameAtMostTwentyMembersAndCountTheRest()
    {
        var payload = "{\"columns\":[" + string.Join(",", Enumerable.Repeat("{}", 25)) + "]}";

        var thrown = FluentActions.Invoking(() => ConduitRuntime.DeserializeWire<Many>(Json(payload)))
            .Should().Throw<ContractMismatchException>().Which;
        thrown.Details.Should().HaveCount(20);
        thrown.Message.Should().EndWith(" and 55 more. Reload to continue.");
    }

    [TestMethod]
    public void AcceptACompleteNestedPayload() =>
        ConduitRuntime.DeserializeWire<Board>(Json(
                """{"title":"b","lead":{"name":"l","wipLimit":null,"done":false},"columns":[{"name":"a","wipLimit":2,"done":true}]}"""))
            .Columns.Should().ContainSingle().Which.WipLimit.Should().Be(2);

    [TestMethod]
    public void LeaveASettablePropertyOfANonPositionalNestedModelUnchecked()
    {
        // The documented bound: only constructor parameters are checked. A positional model inside a list beneath it is.
        ConduitRuntime.DeserializeWire<Wrapper>(Json("""{"inner":{"name":"n"},"contracts":[]}"""))
            .Inner.WipLimit.Should().BeNull();

        var act = () => ConduitRuntime.DeserializeWire<Wrapper>(Json(
            """{"inner":{},"contracts":[{"id":1,"title":"t","due":null}]}"""));
        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("contracts[0].notes");
    }

    [TestMethod]
    public void KeepAWrongTypedNestedFieldABadRequest()
    {
        var act = () => ConduitRuntime.DeserializeWire<Board>(Json(
            """{"title":"b","lead":{"name":"l","wipLimit":"x"},"columns":[]}"""));

        act.Should().Throw<BadRequestException>().Which.Should().NotBeOfType<ContractMismatchException>();
    }

    [TestMethod]
    public void AnswerBadRequestWhenTheLenientReadThrowsSomethingElse()
    {
        // The defaulted (null) Name makes the constructor throw ArgumentNullException: not a 500, a plain 400.
        var act = () => ConduitRuntime.DeserializeWire<Throwing>(Json("{}"));

        act.Should().Throw<BadRequestException>().Which.Should().NotBeOfType<ContractMismatchException>();
    }

    [TestMethod]
    public void NameTheMembersOfANonPublicJsonConstructor()
    {
        var act = () => ConduitRuntime.DeserializeWire<Hidden>(Json("""{"other":"o"}"""));

        act.Should().Throw<ContractMismatchException>().Which.Details!.Select(d => d.Field).Should().Equal("secret");
    }
}
