using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// Verifies the generated mediator hands the runtime its service provider, so a refusal that comes out of
/// <see cref="IWireMediator.HandleWireRequestAsync"/> is written in the reader's language.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RefusalTextThroughTheMediatorShould
{
    private ServiceProvider? serviceProvider;

    [TestInitialize]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddConduit();
        services.AddSingleton<IDispatchCultureResolver>(new DispatchPropertyCultureResolver(
            "lang", new Dictionary<string, CultureInfo> { ["es"] = CultureInfo.GetCultureInfo("es-MX") }));
        services.AddSingleton<IRefusalTextRenderer, SpanishRefusalRenderer>();
        services.AddSingleton<IWireExceptionTranslator, PreflightTranslator>();
        this.serviceProvider = services.BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup()
    {
        MessageContext.RestoreContext(null);
        this.serviceProvider?.Dispose();
    }

    private Task<DispatchResult<object>> DispatchAsync(Type commandType, string lang)
    {
        var wireMediator = this.serviceProvider!.GetRequiredService<IWireMediator>();
        var envelope = new DispatchEnvelope(commandType.FullName!, JsonSerializer.SerializeToElement(new { }));
        MessageContext.StartDispatchFromTransport("m", null, "c", new Dictionary<string, object?> { ["lang"] = lang });
        return wireMediator.HandleWireRequestAsync(envelope, CancellationToken.None);
    }

    [TestMethod]
    public async Task AnswerInTheReadersLanguage()
    {
        // Arrange -- a handler that refuses with the named sentence "orders.shipped"

        // Act
        var result = await this.DispatchAsync(typeof(ShippedOrderCommand), "es");

        // Assert
        result.Error.Should().NotBeNull();
        result.Error!.Message.Should().Be("es:orders.shipped");
    }

    [TestMethod]
    public async Task StillConsultTheRegisteredTranslators()
    {
        // Arrange -- a handler that throws an exception only the registered translator claims

        // Act
        var result = await this.DispatchAsync(typeof(StaleOrderCommand), "es");

        // Assert -- the translator's refusal came back (it carries no Text, so it stays English)
        result.Error.Should().NotBeNull();
        result.Error!.Message.Should().Be("The row changed.");
    }
}
