using System.Globalization;
using AwesomeAssertions;
using Conduit.Exceptions;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class RefusalRenderingShould
{
    private const string English = "Order was already shipped";

    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-MX");

    private sealed class FixedRenderer(string? sentence) : IRefusalTextRenderer
    {
        public string? Render(RefusalText text, CultureInfo culture) => sentence;
    }

    private sealed class ThrowingResolver : IDispatchCultureResolver
    {
        public CultureInfo? Resolve(MessageContext? context) => throw new InvalidOperationException("resolver bug");
    }

    [TestMethod]
    public void RenderTheTextInTheCulture()
    {
        // Arrange
        var logger = new RecordingLogger<RefusalRenderingShould>();

        // Act
        var sentence = RefusalRendering.Render(new RefusalText("orders.shipped"), English, new SpanishRefusalRenderer(), Spanish, logger);

        // Assert
        sentence.Should().Be("es:orders.shipped");
    }

    [TestMethod]
    public void KeepTheEnglishWhenTheRendererAnswersNull()
    {
        // Arrange
        var logger = new RecordingLogger<RefusalRenderingShould>();

        // Act
        var sentence = RefusalRendering.Render(
            new RefusalText("orders.shipped"), English, new SpanishRefusalRenderer(), CultureInfo.GetCultureInfo("en-US"), logger);

        // Assert
        sentence.Should().Be(English);
        logger.Entries.Should().BeEmpty();
    }

    [TestMethod]
    public void KeepTheEnglishWithoutText()
    {
        // Arrange / Act
        var sentence = RefusalRendering.Render(null, English, new SpanishRefusalRenderer(), Spanish, new RecordingLogger<RefusalRenderingShould>());

        // Assert
        sentence.Should().Be(English);
    }

    [TestMethod]
    public void KeepTheEnglishWithoutARenderer()
    {
        // Arrange / Act
        var sentence = RefusalRendering.Render(new RefusalText("orders.shipped"), English, null, Spanish, new RecordingLogger<RefusalRenderingShould>());

        // Assert
        sentence.Should().Be(English);
    }

    [TestMethod]
    public void KeepTheEnglishWithoutACulture()
    {
        // Arrange / Act
        var sentence = RefusalRendering.Render(
            new RefusalText("orders.shipped"), English, new SpanishRefusalRenderer(), null, new RecordingLogger<RefusalRenderingShould>());

        // Assert
        sentence.Should().Be(English);
    }

    [TestMethod]
    public void KeepTheEnglishForAWhitespaceSentence()
    {
        // Arrange / Act
        var sentence = RefusalRendering.Render(
            new RefusalText("orders.shipped"), English, new FixedRenderer("  "), Spanish, new RecordingLogger<RefusalRenderingShould>());

        // Assert
        sentence.Should().Be(English);
    }

    [TestMethod]
    public void KeepTheEnglishAndLogWhenTheRendererThrows()
    {
        // Arrange
        var logger = new RecordingLogger<RefusalRenderingShould>();

        // Act
        var sentence = RefusalRendering.Render(
            new RefusalText(ThrowingRefusalRenderer.FailingName), English, new ThrowingRefusalRenderer(), Spanish, logger);

        // Assert
        sentence.Should().Be(English);
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.EventId.Id.Should().Be(20);
        entry.EventId.Name.Should().Be("RefusalTextRenderFailed");
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("catalog bug");
        entry.Message.Should().Contain(ThrowingRefusalRenderer.FailingName).And.Contain("es-MX");
    }

    [TestMethod]
    public void RenderEachDetailOnItsOwn()
    {
        // Arrange
        var logger = new RecordingLogger<RefusalRenderingShould>();
        List<DispatchErrorDetail> details =
        [
            new("a", "A in English") { Text = new RefusalText("a.name") },
            new("b", "B in English"),
            new("c", "C in English") { Text = new RefusalText(ThrowingRefusalRenderer.FailingName) },
        ];

        // Act
        var rendered = RefusalRendering.RenderDetails(details, new ThrowingRefusalRenderer(), Spanish, logger);

        // Assert
        rendered.Should().NotBeNull();
        rendered.Select(detail => detail.Message).Should().Equal("es:a.name", "B in English", "C in English");
        rendered.Select(detail => detail.Field).Should().Equal("a", "b", "c");
        logger.Entries.Should().ContainSingle().Which.EventId.Id.Should().Be(20);
        details.Select(detail => detail.Message).Should().Equal("A in English", "B in English", "C in English");
    }

    [TestMethod]
    public void ResolveCultureAndLogWhenTheResolverThrows()
    {
        // Arrange
        var logger = new RecordingLogger<RefusalRenderingShould>();

        // Act
        var culture = RefusalRendering.ResolveCulture(new ThrowingResolver(), null, logger);

        // Assert
        culture.Should().BeNull();
        var entry = logger.Entries.Should().ContainSingle().Which;
        entry.EventId.Id.Should().Be(21);
        entry.EventId.Name.Should().Be("DispatchCultureResolveFailed");
        entry.Level.Should().Be(LogLevel.Error);
        entry.Exception.Should().BeOfType<InvalidOperationException>();
    }
}
