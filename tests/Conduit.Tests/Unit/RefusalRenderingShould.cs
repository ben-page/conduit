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
            new("b", "B in English") { Text = new RefusalText(ThrowingRefusalRenderer.FailingName) },
            new("c", "C in English") { Text = new RefusalText("c.name") },
        ];

        // Act
        var rendered = RefusalRendering.RenderDetails(details, new ThrowingRefusalRenderer(), Spanish, logger);

        // Assert
        rendered.Should().NotBeNull();
        rendered.Select(detail => detail.Message).Should().Equal("es:a.name", "B in English", "es:c.name");
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

    private static CountingServiceProvider Provider(
        RecordingLoggerFactory? loggerFactory = null,
        IRefusalTextRenderer? renderer = null,
        IDispatchCultureResolver? resolver = null,
        Type? throwsFor = null)
    {
        var services = new Dictionary<Type, object>();
        if (loggerFactory is not null)
        {
            services[typeof(ILoggerFactory)] = loggerFactory;
        }

        if (renderer is not null)
        {
            services[typeof(IRefusalTextRenderer)] = renderer;
        }

        if (resolver is not null)
        {
            services[typeof(IDispatchCultureResolver)] = resolver;
        }

        return new CountingServiceProvider(services, throwsFor);
    }

    private sealed class FixedResolver(string culture) : IDispatchCultureResolver
    {
        public CultureInfo? Resolve(MessageContext? context) => CultureInfo.GetCultureInfo(culture);
    }

    private static DispatchResult<object> Failure(
        RefusalText? text, IReadOnlyList<DispatchErrorDetail>? details = null) =>
        DispatchResult<object>.Fail(new DetailedRefusal(English, details) { Text = text });

    private sealed class DetailedRefusal(string message, IReadOnlyList<DispatchErrorDetail>? details)
        : ConduitException(ErrorCode.Conflict, message, details);

    [TestMethod]
    public void HandBackASuccessWithoutALookup()
    {
        // Arrange
        var services = Provider();
        var success = DispatchResult<object>.Ok("done");

        // Act
        var result = RefusalRendering.RenderResult(success, services, null);

        // Assert
        result.Should().BeSameAs(success);
        services.Calls.Should().Be(0);
    }

    [TestMethod]
    public void HandBackAFailureWithoutTextAndWithoutALookup()
    {
        // Arrange
        var services = Provider();
        var failure = Failure(text: null, [new DispatchErrorDetail("qty", "Too many")]);

        // Act
        var result = RefusalRendering.RenderResult(failure, services, null);

        // Assert
        result.Should().BeSameAs(failure);
        services.Calls.Should().Be(0);
    }

    [TestMethod]
    public void RenderAMessageWithoutTextWithoutALookup()
    {
        // Arrange
        var services = Provider();

        // Act
        var message = RefusalRendering.RenderMessage(null, English, services, null);

        // Assert
        message.Should().Be(English);
        services.Calls.Should().Be(0);
    }

    [TestMethod]
    public void RenderTheMessageAndTheDetailsInSpanish()
    {
        // Arrange
        var services = Provider(renderer: new SpanishRefusalRenderer(), resolver: new FixedResolver("es-MX"));
        var failure = Failure(
            new RefusalText("orders.shipped"),
            [new DispatchErrorDetail("qty", "Too many") { Text = new RefusalText("qty.max") }]);

        // Act
        var result = RefusalRendering.RenderResult(failure, services, null);

        // Assert
        result.Error!.Message.Should().Be("es:orders.shipped");
        result.Error.Details.Should().ContainSingle().Which.Message.Should().Be("es:qty.max");
        result.Error.Text!.Name.Should().Be("orders.shipped");
        failure.Error!.Message.Should().Be(English);
    }

    [TestMethod]
    public void RenderTheDetailAfterAFailingOneAndLogOnce()
    {
        // Arrange
        var loggerFactory = new RecordingLoggerFactory();
        var services = Provider(loggerFactory, new ThrowingRefusalRenderer(), new FixedResolver("es-MX"));
        var failure = Failure(
            new RefusalText("orders.shipped"),
            [
                new DispatchErrorDetail("a", "A in English") { Text = new RefusalText(ThrowingRefusalRenderer.FailingName) },
                new DispatchErrorDetail("b", "B in English") { Text = new RefusalText("b.name") },
            ]);

        // Act
        var result = RefusalRendering.RenderResult(failure, services, null);

        // Assert
        result.Error!.Message.Should().Be("es:orders.shipped");
        result.Error.Details!.Select(detail => detail.Message).Should().Equal("A in English", "es:b.name");
        loggerFactory.Logger.Entries.Should().ContainSingle().Which.EventId.Id.Should().Be(20);
        loggerFactory.Categories.Should().Contain(typeof(RefusalRendering).FullName!);
    }

    [TestMethod]
    public void CallTheRendererForEnglishToo()
    {
        // Arrange
        var services = Provider(renderer: new FixedRenderer("Catalog English"), resolver: new FixedResolver("en-US"));

        // Act
        var message = RefusalRendering.RenderMessage(new RefusalText("orders.shipped"), English, services, null);

        // Assert
        message.Should().Be("Catalog English");
    }

    [TestMethod]
    public void AnswerEnglishAndLogWhenTheResolverCannotBeResolved()
    {
        // Arrange
        var loggerFactory = new RecordingLoggerFactory();
        var services = Provider(loggerFactory, new SpanishRefusalRenderer(), throwsFor: typeof(IDispatchCultureResolver));

        // Act
        var message = RefusalRendering.RenderMessage(new RefusalText("orders.shipped"), English, services, null);

        // Assert
        message.Should().Be(English);
        var entry = loggerFactory.Logger.Entries.Should().ContainSingle().Which;
        entry.EventId.Id.Should().Be(21);
        entry.Level.Should().Be(LogLevel.Error);
    }

    [TestMethod]
    public void AnswerEnglishAndLogWhenTheRendererCannotBeResolved()
    {
        // Arrange
        var loggerFactory = new RecordingLoggerFactory();
        var services = Provider(loggerFactory, resolver: new FixedResolver("es-MX"), throwsFor: typeof(IRefusalTextRenderer));

        // Act
        var message = RefusalRendering.RenderMessage(new RefusalText("orders.shipped"), English, services, null);

        // Assert
        message.Should().Be(English);
        var entry = loggerFactory.Logger.Entries.Should().ContainSingle().Which;
        entry.EventId.Id.Should().Be(20);
        entry.Message.Should().Contain("orders.shipped").And.Contain("es-MX");
    }
}
