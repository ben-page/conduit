using System.Text.Json;
using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Exceptions;
using Conduit.Messages;
using Conduit.Runtime;

namespace Conduit.Tests.Unit;

/// <summary>
/// A closed set (<see cref="IClosedSet{TSelf}"/> + <see cref="ClosedSetAttribute"/>) crosses the wire as its bare value:
/// written as the string, read back to the very same member, and any other spelling refused as a 400 naming its JSON
/// path -- the enum rule, for a set whose spellings are not C# identifiers. A <c>[ClosedSet]</c> list that disagrees with
/// <c>All</c> fails the first serialization, so a generated client (built from the attribute) can never silently
/// disagree with the wire (built from <c>All</c>).
/// </summary>
[TestClass]
public sealed class ClosedSetWireShould
{
#pragma warning disable CA1034 // test fixtures
    [ClosedSet("self", "reports", "any")]
    public sealed record Reach : IClosedSet<Reach>
    {
        public static readonly Reach Self = new("self");
        public static readonly Reach Reports = new("reports");
        public static readonly Reach Any = new("any");

        private Reach(string value) => this.Value = value;

        public static IReadOnlyList<Reach> All { get; } = [Self, Reports, Any];

        public string Value { get; }
    }

    public sealed record Grant(string Key, Reach Reach, Reach? Widest);

    public sealed record Counts(IReadOnlyDictionary<Reach, int> ByReach);

    // The attribute names "b"; All spells "c".
    [ClosedSet("a", "b")]
    public sealed record Drifted : IClosedSet<Drifted>
    {
        public static readonly Drifted A = new("a");
        public static readonly Drifted C = new("c");

        private Drifted(string value) => this.Value = value;

        public static IReadOnlyList<Drifted> All { get; } = [A, C];

        public string Value { get; }
    }

    // A closed set that never states its values for TypeGen.
    public sealed record Unlisted : IClosedSet<Unlisted>
    {
        public static readonly Unlisted Only = new("only");

        private Unlisted(string value) => this.Value = value;

        public static IReadOnlyList<Unlisted> All { get; } = [Only];

        public string Value { get; }
    }
#pragma warning restore CA1034

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [TestMethod]
    public void WriteAMemberAsItsBareValue()
    {
        // Act
        var json = JsonSerializer.Serialize(new Grant("k", Reach.Reports, null), ConduitRuntime.JsonOptions);

        // Assert
        json.Should().Be("""{"key":"k","reach":"reports","widest":null}""");
    }

    [TestMethod]
    public void ReadAValueBackToTheSameMember()
    {
        // Act
        var grant = ConduitRuntime.DeserializeWire<Grant>(Json("""{"key":"k","reach":"any","widest":"self"}"""));

        // Assert
        grant.Reach.Should().BeSameAs(Reach.Any);
        grant.Widest.Should().BeSameAs(Reach.Self);
    }

    [TestMethod]
    [DataRow("""{"key":"k","reach":"SELF","widest":null}""")]
    [DataRow("""{"key":"k","reach":"company","widest":null}""")]
    [DataRow("""{"key":"k","reach":"","widest":null}""")]
    [DataRow("""{"key":"k","reach":1,"widest":null}""")]
    [DataRow("""{"key":"k","reach":{"value":"self"},"widest":null}""")]
    public void RefuseAnythingButAMembersSpelling_AtItsPath(string payload)
    {
        // Act -- the wrong case (Ordinal), a stale spelling, empty, a number, and the object System.Text.Json wrote
        // before this converter existed.
        var act = () => ConduitRuntime.DeserializeWire<Grant>(Json(payload));

        // Assert
        act.Should().Throw<BadRequestException>().Which.Message.Should().Be("The request could not be read at '$.reach'.");
    }

    [TestMethod]
    public void ReadAJsonNullAsNull()
    {
        // Act -- Conduit does not enforce nullable annotations; whether a member may be absent is the declaring
        // contract's to refuse (its Validate()).
        var grant = ConduitRuntime.DeserializeWire<Grant>(Json("""{"key":"k","reach":null,"widest":null}"""));

        // Assert
        grant.Reach.Should().BeNull();
    }

    [TestMethod]
    public void KeyADictionaryByValue()
    {
        // Arrange
        var counts = new Counts(new Dictionary<Reach, int> { [Reach.Self] = 2 });

        // Act
        var json = JsonSerializer.Serialize(counts, ConduitRuntime.JsonOptions);
        var back = JsonSerializer.Deserialize<Counts>(json, ConduitRuntime.JsonOptions)!;

        // Assert
        json.Should().Be("""{"byReach":{"self":2}}""");
        back.ByReach.Should().ContainKey(Reach.Self).WhoseValue.Should().Be(2);
    }

    [TestMethod]
    public void CarryTheValueInTheAuditPayload()
    {
        // Act
        var json = JsonSerializer.Serialize(new Grant("k", Reach.Any, null), ConduitRuntime.AuditJsonOptions);

        // Assert
        json.Should().Contain("\"reach\":\"any\"");
    }

    [TestMethod]
    public void RefuseADeclarationThatDisagreesWithAll()
    {
        // Act
        var act = () => JsonSerializer.Serialize(Drifted.A, ConduitRuntime.JsonOptions);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*[ClosedSet] lists (a, b) but All spells (a, c)*");
    }

    [TestMethod]
    public void RefuseAClosedSetThatDoesNotDeclareItsValues()
    {
        // Act
        var act = () => JsonSerializer.Serialize(Unlisted.Only, ConduitRuntime.JsonOptions);

        // Assert
        act.Should().Throw<InvalidOperationException>().WithMessage("*without [ClosedSet(...)]*");
    }

    [TestMethod]
    public void LeaveEveryOtherTypeAlone()
    {
        // Arrange
        var factory = new ClosedSetJsonConverterFactory();

        // Act + Assert
        factory.CanConvert(typeof(string)).Should().BeFalse();
        factory.CanConvert(typeof(Grant)).Should().BeFalse();
        factory.CanConvert(typeof(Reach)).Should().BeTrue();
    }
}
