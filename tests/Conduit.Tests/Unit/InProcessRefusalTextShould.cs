using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Generated;
using Conduit.Middleware;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conduit.Tests.Unit;

[TestClass]
public sealed class InProcessRefusalTextShould
{
    private sealed class StaleWrite : Exception;

    private sealed class DetailedRefusal(string message, IReadOnlyList<DispatchErrorDetail> details)
        : ConduitException(ErrorCode.Conflict, message, details);

    private sealed class TextTranslator : IWireExceptionTranslator
    {
        public ConduitException? Translate(Exception exception) =>
            exception is StaleWrite
                ? new PreconditionFailedException("Someone else changed this.") { Text = new RefusalText("stale") }
                : null;
    }

    private static ServiceProvider Services(bool withTranslator = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDispatchCultureResolver>(new DispatchPropertyCultureResolver(
            "lang", new Dictionary<string, CultureInfo> { ["es"] = CultureInfo.GetCultureInfo("es-MX") }));
        services.AddSingleton<IRefusalTextRenderer, SpanishRefusalRenderer>();
        if (withTranslator)
        {
            services.AddSingleton<IWireExceptionTranslator, TextTranslator>();
        }

        return services.BuildServiceProvider();
    }

    private static async Task<DispatchResult<object>> DispatchAsync(Exception toThrow, string? lang, bool withTranslator = false)
    {
        var previous = MessageContext.Current;
        try
        {
            if (lang is not null)
            {
                MessageContext.StartDispatchFromTransport("m", null, "c", new Dictionary<string, object?> { ["lang"] = lang });
            }

            using var services = Services(withTranslator);
            return await ConduitRuntime.HandleWireRequest(
                "Test.Refuse",
                [],
                () => new DispatchEnvelope("Test.Refuse", JsonSerializer.SerializeToElement(new { })),
                (_, _, _) => throw toThrow,
                NullLogger.Instance,
                services,
                CancellationToken.None);
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    private static ConflictException Shipped() =>
        new("Order was already shipped") { Text = new RefusalText("orders.shipped") };

    [TestMethod]
    public async Task AnswerARefusalInEnglishWithItsTextUnderASpanishReader()
    {
        // Arrange / Act
        var result = await DispatchAsync(Shipped(), "es");

        // Assert
        result.Error!.Message.Should().Be("Order was already shipped");
        result.Error.Text!.Name.Should().Be("orders.shipped");
        result.Error.Code.Should().Be("Conflict");
        result.Error.Status.Should().Be(409);
    }

    [TestMethod]
    public async Task KeepTheDetailsEnglishEachWithItsText()
    {
        // Arrange
        var refusal = new DetailedRefusal(
            "Order was invalid", [new DispatchErrorDetail("qty", "Too many") { Text = new RefusalText("qty.max") }])
        {
            Text = new RefusalText("orders.invalid"),
        };

        // Act
        var result = await DispatchAsync(refusal, "es");

        // Assert
        result.Error!.Message.Should().Be("Order was invalid");
        var detail = result.Error.Details.Should().ContainSingle().Which;
        detail.Message.Should().Be("Too many");
        detail.Text!.Name.Should().Be("qty.max");
        detail.Field.Should().Be("qty");
    }

    [TestMethod]
    public async Task LeaveTheExceptionEnglish()
    {
        // Arrange
        var detail = new DispatchErrorDetail("qty", "Too many") { Text = new RefusalText("qty.max") };
        var refusal = new DetailedRefusal("Order was already shipped", [detail])
        {
            Text = new RefusalText("orders.shipped"),
        };

        // Act
        await DispatchAsync(refusal, "es");

        // Assert
        refusal.Message.Should().Be("Order was already shipped");
        refusal.Details.Should().ContainSingle().Which.Should().BeSameAs(detail);
        detail.Message.Should().Be("Too many");
    }

    [TestMethod]
    public async Task AnswerInEnglishWithoutTheProperty()
    {
        // Arrange / Act
        var result = await DispatchAsync(Shipped(), lang: null);

        // Assert
        result.Error!.Message.Should().Be("Order was already shipped");
        result.Error.Text!.Name.Should().Be("orders.shipped");
    }

    [TestMethod]
    public async Task CarryTheTextOfATranslatedRefusal()
    {
        // Arrange / Act
        var result = await DispatchAsync(new StaleWrite(), "es", withTranslator: true);

        // Assert
        result.Error!.Message.Should().Be("Someone else changed this.");
        result.Error.Text!.Name.Should().Be("stale");
        result.Error.Code.Should().Be("PreconditionFailed");
    }

    [TestMethod]
    public async Task AnswerTheUnexpectedErrorInEnglishWithItsTextAndReference()
    {
        // Arrange / Act
        var result = await DispatchAsync(new InvalidOperationException("SELECT * FROM secrets"), "es");

        // Assert
        result.Error!.Message.Should().Be("An unexpected error occurred.");
        result.Error.Text!.Name.Should().Be(ConduitTextNames.UnexpectedError);
        result.Error.Reference.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public async Task SetTheActivityStatusFromTheEnglishMessage()
    {
        // Arrange
        string? statusDescription = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Conduit",
            Sample = SampleAllData,
            ActivityStopped = activity => statusDescription = activity.StatusDescription,
        };
        ActivitySource.AddActivityListener(listener);

        // Act
        await DispatchAsync(Shipped(), "es");

        // Assert
        statusDescription.Should().Be("Order was already shipped");
    }

    private static ActivitySamplingResult SampleAllData(ref ActivityCreationOptions<ActivityContext> _) =>
        ActivitySamplingResult.AllDataAndRecorded;

    [TestMethod]
    public async Task AnswerAPreflightRefusalInEnglishWithItsText()
    {
        // Arrange
        await using var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddConduit(conduit => conduit.AddPipeline(typeof(PreflightTextCheckBehavior<,>)));
            builder.Services.AddSingleton<IDispatchCultureResolver>(new DispatchPropertyCultureResolver(
                "lang", new Dictionary<string, CultureInfo> { ["es"] = CultureInfo.GetCultureInfo("es-MX") }));
            builder.Services.AddSingleton<IRefusalTextRenderer, SpanishRefusalRenderer>();
        });
        var payload = JsonSerializer.SerializeToElement(new PreflightCommand("admit"), ConduitRuntime.JsonOptions);
        var previous = MessageContext.Current;

        try
        {
            MessageContext.StartDispatchFromTransport("m", null, "c", new Dictionary<string, object?> { ["lang"] = "es" });

            // Act
            var preflight = await ConduitRuntime.PreflightWireRequestAsync(
                typeof(PreflightCommand).FullName!, payload, host.Services, CancellationToken.None);

            // Assert
            preflight.Admitted.Should().BeFalse();
            preflight.Refusal!.Message.Should().Be("Order was already shipped");
            preflight.Refusal.Text!.Name.Should().Be("orders.shipped");
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }

    [TestMethod]
    public async Task KeepTheOldOverloadsEnglish()
    {
        // Arrange
        var previous = MessageContext.Current;
        try
        {
            MessageContext.StartDispatchFromTransport("m", null, "c", new Dictionary<string, object?> { ["lang"] = "es" });

            // Act
            var result = await ConduitRuntime.HandleWireRequest(
                "Test.Refuse",
                [],
                () => new DispatchEnvelope("Test.Refuse", JsonSerializer.SerializeToElement(new { })),
                (_, _, _) => throw Shipped(),
                NullLogger.Instance,
                new IWireExceptionTranslator[] { new TextTranslator() },
                CancellationToken.None);

            // Assert
            result.Error!.Message.Should().Be("Order was already shipped");
        }
        finally
        {
            MessageContext.RestoreContext(previous);
        }
    }
}
