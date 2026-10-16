using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Messages;
using Conduit.Runtime;
using Conduit.Tests.Support;

namespace Conduit.Tests.Unit;

/// <summary>
/// An [AuditAs] member is typed at capture: the sibling names a registered message type, the JSON is read as it, and the
/// audit payload carries that message redacted by its own marks -- or the placeholder, never the raw JSON.
/// </summary>
[TestClass]
public sealed class AuditAsShould
{
    private const string Secret = "4321";

    private const string Ssn = "123-45-6789";

    [ClassInitialize]
    public static void RegisterInnerMessages(TestContext testContext)
    {
        // A registry adds its message types to the process-wide lookup AuditJsonOptions resolves against
        _ = new ConduitTypeRegistry(
            eventTypes: [],
            commandTypes: [typeof(AuditAsInnerCommand), typeof(WholeSecretCommand), typeof(AuditAsThrowingInnerCommand)],
            queryTypes: []);
    }

    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Capture(AuditAsItem item) => JsonSerializer.SerializeToElement(item, ConduitRuntime.AuditJsonOptions);

    [TestMethod]
    public void RedactAKnownInnerMessageByItsOwnMarks()
    {
        // Arrange
        var item = new AuditAsItem(
            typeof(AuditAsInnerCommand).FullName, Element(new { name = "inner-visible", pin = Secret, ssn = Ssn }));

        // Act
        var json = Capture(item);

        // Assert
        var payload = json.GetProperty("payload");
        payload.GetProperty("name").GetString().Should().Be("inner-visible");
        payload.TryGetProperty("pin", out _).Should().BeFalse("the inner [DoNotAudit] member is left out");
        payload.GetProperty("ssn").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        json.GetRawText().Should().NotContain(Secret).And.NotContain(Ssn);
    }

    [TestMethod]
    public void WriteThePlaceholderForAnUnknownTypeName()
    {
        // Arrange
        var item = new AuditAsItem("No.Such.Command", Element(new { pin = Secret }));

        // Act
        var json = Capture(item);

        // Assert
        json.GetProperty("payload").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        json.GetRawText().Should().NotContain(Secret);
    }

    [TestMethod]
    public void WriteThePlaceholderForANullTypeName()
    {
        // Arrange
        var item = new AuditAsItem(null, Element(new { pin = Secret }));

        // Act
        var json = Capture(item);

        // Assert
        json.GetProperty("payload").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        json.GetRawText().Should().NotContain(Secret);
    }

    [TestMethod]
    public void WriteThePlaceholderForJsonThatDoesNotReadAsTheType()
    {
        // Arrange -- `name` must be a string
        var item = new AuditAsItem(typeof(AuditAsInnerCommand).FullName, Element(new { name = new { pin = Secret } }));

        // Act
        var json = Capture(item);

        // Assert
        json.GetProperty("payload").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        json.GetRawText().Should().NotContain(Secret);
    }

    [TestMethod]
    public void WriteThePlaceholderForATypeNameThatIsNotARegisteredMessage()
    {
        // Arrange -- System.String reads "4321" happily; only a registered message type may be resolved
        var item = new AuditAsItem(typeof(string).FullName, Element(Secret));

        // Act
        var json = Capture(item);

        // Assert
        json.GetProperty("payload").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        json.GetRawText().Should().NotContain(Secret);
    }

    [TestMethod]
    public void WriteNullForANullOrUndefinedElement()
    {
        // Arrange
        var jsonNull = new AuditAsItem(typeof(AuditAsInnerCommand).FullName, Element(null!));
        var undefined = new AuditAsItem(typeof(AuditAsInnerCommand).FullName, default);

        // Act
        var fromNull = Capture(jsonNull);
        var fromUndefined = Capture(undefined);

        // Assert
        fromNull.GetProperty("payload").ValueKind.Should().Be(JsonValueKind.Null);
        fromUndefined.GetProperty("payload").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public void LeaveOutAnInnerMessageMarkedDoNotAuditAtTypeLevel()
    {
        // Arrange
        var item = new AuditAsItem(typeof(WholeSecretCommand).FullName, Element(new { secret = Secret }));

        // Act
        var json = Capture(item);

        // Assert
        json.TryGetProperty("payload", out _).Should().BeFalse("a type-level [DoNotAudit] message captures no body");
        json.GetProperty("type").GetString().Should().Be(typeof(WholeSecretCommand).FullName);
        json.GetRawText().Should().NotContain(Secret);
    }

    [TestMethod]
    public void TypeANullableElementMarkedOnTheProperty()
    {
        // Arrange
        var item = new AuditAsPropertyItem
        {
            Kind = typeof(AuditAsInnerCommand).FullName,
            Body = Element(new { name = "inner-visible", pin = Secret, ssn = Ssn }),
        };

        // Act
        var json = JsonSerializer.SerializeToElement(item, ConduitRuntime.AuditJsonOptions);

        // Assert
        var body = json.GetProperty("body");
        body.GetProperty("name").GetString().Should().Be("inner-visible");
        body.TryGetProperty("pin", out _).Should().BeFalse();
        body.GetProperty("ssn").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
    }

    [TestMethod]
    public void RedactAnItemNestedInAMessage()
    {
        // Arrange
        var envelope = new AuditAsEnvelopeCommand(
            [new AuditAsItem(typeof(AuditAsInnerCommand).FullName, Element(new { name = "inner-visible", pin = Secret, ssn = Ssn }))]);

        // Act
        var json = JsonSerializer.SerializeToElement(envelope, ConduitRuntime.AuditJsonOptions);

        // Assert
        var payload = json.GetProperty("items")[0].GetProperty("payload");
        payload.GetProperty("name").GetString().Should().Be("inner-visible");
        payload.GetProperty("ssn").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        json.GetRawText().Should().NotContain(Secret).And.NotContain(Ssn);
    }

    [TestMethod]
    public void LeaveTheWireOptionsUntouched()
    {
        // Arrange
        var item = new AuditAsItem(typeof(AuditAsInnerCommand).FullName, Element(new { name = "n", pin = Secret, ssn = Ssn }));

        // Act
        var json = JsonSerializer.SerializeToElement(item, ConduitRuntime.JsonOptions);

        // Assert
        json.GetProperty("payload").GetProperty("pin").GetString().Should().Be(Secret);
    }

    [TestMethod]
    public void WriteThePlaceholderWhenTheInnerContractThrowsOnRead()
    {
        // Arrange -- the constructor throws ArgumentException, which no JSON-specific catch would see
        var item = new AuditAsItem(typeof(AuditAsThrowingInnerCommand).FullName, Element(new { value = "refused" }));

        // Act
        var json = Capture(item);

        // Assert
        json.GetProperty("payload").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
    }

    [TestMethod]
    public void ResolveANewHiddenSiblingToTheDerivedDeclaration()
    {
        // Arrange -- Type.GetProperty would throw AmbiguousMatchException on the hidden pair
        var item = new AuditAsHiddenItem
        {
            Type = typeof(AuditAsInnerCommand).FullName,
            Payload = Element(new { name = "inner-visible", pin = Secret, ssn = Ssn }),
        };

        // Act
        var json = JsonSerializer.SerializeToElement(item, ConduitRuntime.AuditJsonOptions);

        // Assert
        var payload = json.GetProperty("payload");
        payload.GetProperty("name").GetString().Should().Be("inner-visible");
        payload.TryGetProperty("pin", out _).Should().BeFalse();
        json.GetRawText().Should().NotContain(Secret).And.NotContain(Ssn);
    }

    [TestMethod]
    public void WriteThePlaceholderForANameTwoTypesRegistered()
    {
        // Arrange -- two distinct message types with one FullName, as two versions of a contracts assembly would be
        const string sharedName = "Conduit.Tests.Dynamic.SharedNameEvent";
        var item = new AuditAsItem(sharedName, Element(new { }));
        _ = new ConduitTypeRegistry(eventTypes: [DynamicEvent(sharedName)], commandTypes: [], queryTypes: []);
        var beforeTheSecond = Capture(item);

        // Act
        _ = new ConduitTypeRegistry(eventTypes: [DynamicEvent(sharedName)], commandTypes: [], queryTypes: []);
        var json = Capture(item);

        // Assert
        beforeTheSecond.GetProperty("payload").ValueKind.Should().Be(JsonValueKind.Object, "one registered type resolves");
        json.GetProperty("payload").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
    }

    private static Type DynamicEvent(string fullName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName($"Dynamic{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("Main")
            .DefineType(fullName, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object), [typeof(IEvent)]);
        type.DefineDefaultConstructor(MethodAttributes.Public);
        return type.CreateType();
    }
}
