using System.Text.Json;
using AwesomeAssertions;
using Conduit.Audit.Tests.Support;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Tests.Integration;

/// <summary>
/// An [AuditAs] member honours the host's audit registration: a message whose body AddAudit suppresses
/// (SuppressPayload&lt;T&gt; or Suppress&lt;T&gt;) is left out when another message carries it, never written in full.
/// </summary>
[TestClass]
public sealed class AuditAsSuppressionShould
{
    private const string BirthDate = "1980-02-29";

    [ClassInitialize]
    public static void RegisterAudit(TestContext testContext)
    {
        // The inner messages are registered message types, and the host's audit registration suppresses two of them
        _ = new ConduitTypeRegistry(
            eventTypes: [typeof(PayloadSuppressedEvent), typeof(FullySuppressedEvent), typeof(CapturedEvent)],
            commandTypes: [],
            queryTypes: []);
        new ConduitBuilder(new ServiceCollection()).AddAudit(audit => audit
            .SuppressPayload<PayloadSuppressedEvent>()
            .Suppress<FullySuppressedEvent>());
    }

    private static JsonElement Capture(Type inner, object payload) =>
        JsonSerializer.SerializeToElement(
            new AuditAsTestItem(inner.FullName!, JsonSerializer.SerializeToElement(payload)), ConduitRuntime.AuditJsonOptions);

    [TestMethod]
    public void LeaveOutAnInnerMessageWhosePayloadIsSuppressed()
    {
        // Act
        var json = Capture(typeof(PayloadSuppressedEvent), new { birthDate = BirthDate });

        // Assert
        json.TryGetProperty("payload", out _).Should().BeFalse("SuppressPayload<T>() keeps the body out wherever it travels");
        json.GetRawText().Should().NotContain(BirthDate);
    }

    [TestMethod]
    public void LeaveOutAnInnerMessageThatIsFullySuppressed()
    {
        // Act
        var json = Capture(typeof(FullySuppressedEvent), new { birthDate = BirthDate });

        // Assert
        json.TryGetProperty("payload", out _).Should().BeFalse();
        json.GetRawText().Should().NotContain(BirthDate);
    }

    [TestMethod]
    public void CaptureAnInnerMessageNoRegistrationSuppresses()
    {
        // Act
        var json = Capture(typeof(CapturedEvent), new { note = "kept" });

        // Assert
        json.GetProperty("payload").GetProperty("note").GetString().Should().Be("kept");
    }
}
