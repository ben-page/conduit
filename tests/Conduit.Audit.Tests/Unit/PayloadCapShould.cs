using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Conduit.Attributes;
using Conduit.Audit.Configuration;
using Conduit.Audit.Runtime;
using Conduit.Audit.Tests.Support;
using Conduit.Runtime;

namespace Conduit.Audit.Tests.Unit;

/// <summary>
/// A sealed record's payload is capped at <see cref="AuditOptions.MaxPayloadBytes"/> UTF-8 bytes: a body over it is
/// stored with its longest strings shortened and a <c>$truncated</c> marker, or as the marker alone when that cannot fit.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PayloadCapShould(TestContext testContext)
{
    private const string MessageType = "Test.Commands.Capped";
    private const int Kilobyte = 1024;
    private static readonly string[] Tags = ["a", "b"];

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
    }

    private static DispatchAuditRecord Seal(
        JsonElement payload,
        int maxPayloadBytes = AuditOptions.DefaultMaxPayloadBytes,
        IReadOnlySet<string>? suppressedPayloadTypes = null)
    {
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(
            channel.Writer, "TestService", new HashSet<string>(), suppressedPayloadTypes ?? new HashSet<string>(), maxPayloadBytes);
        var context = MessageContext.StartDispatch();
        context.SetPayload(payload);
        observer.OnObservation(new DispatchInvoked(MessageType, "Command"), context);
        observer.OnObservation(new DispatchCompleted(1.0), context);
        channel.Reader.TryRead(out var record).Should().BeTrue();
        return record!;
    }

    private static JsonElement Capture(object body) => JsonSerializer.SerializeToElement(body, ConduitRuntime.AuditJsonOptions);

    private static int Utf8Length(JsonElement element) => Encoding.UTF8.GetByteCount(element.GetRawText());

    // The stored body re-parsed from its raw text: proves it is valid JSON on its own.
    private static JsonElement Reparse(DispatchAuditRecord record)
    {
        record.Payload.Should().NotBeNull();
        using var document = JsonDocument.Parse(record.Payload.Value.GetRawText());
        return document.RootElement.Clone();
    }

    private static void ShouldBeMarkerOnly(JsonElement stored, int originalBytes, int cap)
    {
        stored.ValueKind.Should().Be(JsonValueKind.Object);
        stored.EnumerateObject().Select(property => property.Name).Should().Equal("$truncated");
        stored.GetProperty("$truncated").GetProperty("bytes").GetInt64().Should().Be(originalBytes);
        stored.GetProperty("$truncated").GetProperty("cap").GetInt32().Should().Be(cap);
    }

    [TestMethod]
    public void KeepABodyUnderTheCapByteForByte()
    {
        // Arrange -- a redacted placeholder and non-ASCII text, both escaped by the capture serializer
        var payload = Capture(new RedactedNote("123-45-6789", "Café résumé", ["one", "two"]));

        // Act
        var record = Seal(payload);

        // Assert
        record.Payload!.Value.GetRawText().Should().Be(payload.GetRawText());
    }

    [TestMethod]
    public void KeepABodyExactlyAtTheCapByteForByte()
    {
        // Arrange
        var payload = Capture(new { description = new string('x', 3 * Kilobyte) });
        var cap = Utf8Length(payload);

        // Act
        var atCap = Seal(payload, cap);
        var overCap = Seal(payload, cap - 1);

        // Assert
        atCap.Payload!.Value.GetRawText().Should().Be(payload.GetRawText());
        overCap.Payload!.Value.GetRawText().Should().NotBe(payload.GetRawText());
    }

    [TestMethod]
    public void ShortenAOneMegabyteStringUnderTheDefaultCap()
    {
        // Arrange
        var description = "BEGIN-" + new string('d', 1024 * Kilobyte);
        var payload = Capture(new { id = 7, title = "Short title", description, tags = Tags });
        var originalBytes = Utf8Length(payload);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var record = Seal(payload);
        stopwatch.Stop();
        testContext.WriteLine($"Sealing a {originalBytes:N0}-byte body under a {AuditOptions.DefaultMaxPayloadBytes:N0}-byte cap took {stopwatch.Elapsed.TotalMilliseconds:F1} ms");

        // Assert
        Utf8Length(record.Payload!.Value).Should().BeLessThanOrEqualTo(AuditOptions.DefaultMaxPayloadBytes);
        var stored = Reparse(record);
        stored.GetProperty("id").GetInt32().Should().Be(7);
        stored.GetProperty("title").GetString().Should().Be("Short title");
        stored.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("a", "b");
        var shortened = stored.GetProperty("description").GetString()!;
        shortened.Should().StartWith("BEGIN-ddd").And.EndWith("…");
        shortened.Length.Should().BeGreaterThan(60 * Kilobyte, "the cut keeps as much of the string as the cap allows");
        stored.GetProperty("$truncated").GetProperty("bytes").GetInt64().Should().Be(originalBytes);
        stored.GetProperty("$truncated").GetProperty("cap").GetInt32().Should().Be(AuditOptions.DefaultMaxPayloadBytes);
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "the cut is a binary search, not a character-at-a-time loop");
    }

    [TestMethod]
    public void ShortenNestedLongStringsInAnArrayOfObjects()
    {
        // Arrange
        var payload = Capture(new
        {
            name = "batch",
            items = new[]
            {
                new { name = "first", note = "first-" + new string('n', 5000) },
                new { name = "second", note = "second-" + new string('m', 5000) },
            },
        });

        // Act
        var record = Seal(payload, Kilobyte);

        // Assert
        Utf8Length(record.Payload!.Value).Should().BeLessThanOrEqualTo(Kilobyte);
        var stored = Reparse(record);
        stored.GetProperty("name").GetString().Should().Be("batch");
        var items = stored.GetProperty("items").EnumerateArray().ToList();
        items.Select(item => item.GetProperty("name").GetString()).Should().Equal("first", "second");
        items[0].GetProperty("note").GetString().Should().StartWith("first-n").And.EndWith("…");
        items[1].GetProperty("note").GetString().Should().StartWith("second-m").And.EndWith("…");
        stored.GetProperty("$truncated").GetProperty("cap").GetInt32().Should().Be(Kilobyte);
    }

    [TestMethod]
    public void NeverShortenARedactedPlaceholder()
    {
        // Arrange -- so many strings that the common cut goes below the placeholder's three characters
        var lines = Enumerable.Range(0, 90).Select(_ => new string('x', 50)).ToList();
        var payload = Capture(new RedactedNote("123-45-6789", "Title", lines));

        // Act
        var record = Seal(payload, Kilobyte);

        // Assert
        Utf8Length(record.Payload!.Value).Should().BeLessThanOrEqualTo(Kilobyte);
        var stored = Reparse(record);
        stored.GetProperty("ssn").GetString().Should().Be(AuditRedactedAttribute.Placeholder);
        stored.GetProperty("lines").EnumerateArray().Should().AllSatisfy(
            line => line.GetString().Should().EndWith("…").And.HaveLength(2, "a kept prefix of one character is shorter than the placeholder"));
    }

    [TestMethod]
    public void NeverSplitASurrogatePair()
    {
        // Arrange
        var payload = Capture(new { text = string.Concat(Enumerable.Repeat("\U0001F600", 4000)) });

        // Act
        var record = Seal(payload, Kilobyte);

        // Assert
        Utf8Length(record.Payload!.Value).Should().BeLessThanOrEqualTo(Kilobyte);
        var text = Reparse(record).GetProperty("text").GetString()!;
        text.Should().EndWith("…");
        var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var encode = () => strictUtf8.GetByteCount(text);
        encode.Should().NotThrow("a lone surrogate would mean the cut split a pair");
    }

    [TestMethod]
    public void StoreOnlyTheMarkerForANonObjectRoot()
    {
        // Arrange
        var payload = Capture(Enumerable.Range(0, 10).Select(index => $"{index}-" + new string('a', 500)).ToArray());
        var originalBytes = Utf8Length(payload);

        // Act
        var record = Seal(payload, Kilobyte);

        // Assert
        Utf8Length(record.Payload!.Value).Should().BeLessThanOrEqualTo(Kilobyte);
        ShouldBeMarkerOnly(Reparse(record), originalBytes, Kilobyte);
    }

    [TestMethod]
    public void StoreOnlyTheMarkerWhenShorteningStringsCannotFit()
    {
        // Arrange -- many small numbers: no string to shorten
        var payload = Capture(new { label = "numbers", values = Enumerable.Range(0, 2000).ToArray() });
        var originalBytes = Utf8Length(payload);

        // Act
        var record = Seal(payload, Kilobyte);

        // Assert
        Utf8Length(record.Payload!.Value).Should().BeLessThanOrEqualTo(Kilobyte);
        ShouldBeMarkerOnly(Reparse(record), originalBytes, Kilobyte);
    }

    [TestMethod]
    public void StoreTheFullBodyWhenTheCapIsDisabled()
    {
        // Arrange
        var payload = Capture(new { description = new string('d', 1024 * Kilobyte) });

        // Act
        var record = Seal(payload, maxPayloadBytes: 0);

        // Assert
        record.Payload!.Value.GetRawText().Should().Be(payload.GetRawText());
    }

    [TestMethod]
    public void StoreNoBodyForASuppressedPayloadTypeOverTheCap()
    {
        // Arrange
        var payload = Capture(new { description = new string('d', 100 * Kilobyte) });

        // Act
        var record = Seal(payload, suppressedPayloadTypes: new HashSet<string> { MessageType });

        // Assert
        record.Payload.Should().BeNull();
    }

    [TestMethod]
    public void CapABodySealedByStaleEviction()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();
        var observer = new AuditObserver(channel.Writer, "TestService", new HashSet<string>(), new HashSet<string>(), Kilobyte);
        var context = MessageContext.StartDispatch();
        observer.OnObservation(new DispatchInvoked(MessageType, "Request"), context);
        context.SetPayload(Capture(new { description = new string('d', 10 * Kilobyte) }));

        // Act
        observer.EvictStaleGroups(DateTime.UtcNow.AddMinutes(10), TimeSpan.FromMinutes(5));

        // Assert
        channel.Reader.TryRead(out var record).Should().BeTrue();
        Utf8Length(record!.Payload!.Value).Should().BeLessThanOrEqualTo(Kilobyte);
        Reparse(record).GetProperty("description").GetString().Should().EndWith("…");
    }

    [TestMethod]
    public void RefuseACapTooSmallForTheMarker()
    {
        // Arrange
        var channel = Channel.CreateUnbounded<DispatchAuditRecord>();

        // Act
        var act = () => new AuditObserver(channel.Writer, "TestService", new HashSet<string>(), new HashSet<string>(), 100);

        // Assert
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void ReplaceABodysOwnTruncatedMemberOverTheCap()
    {
        // Arrange -- a message whose root really has a "$truncated" member
        var payload = Capture(new Dictionary<string, object>
        {
            ["$truncated"] = "the message's own value",
            ["description"] = new string('d', 10 * Kilobyte),
        });
        var originalBytes = Utf8Length(payload);

        // Act
        var record = Seal(payload, Kilobyte);

        // Assert -- the cap's marker takes its place; the message's own value is not kept
        var stored = Reparse(record);
        stored.EnumerateObject().Count(property => property.Name == "$truncated").Should().Be(1);
        stored.GetProperty("$truncated").GetProperty("bytes").GetInt64().Should().Be(originalBytes);
        stored.GetRawText().Should().NotContain("the message's own value");
        stored.GetProperty("description").GetString().Should().EndWith("…");
    }
}
