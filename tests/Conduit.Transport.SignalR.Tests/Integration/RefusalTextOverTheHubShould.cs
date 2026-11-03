using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class RefusalTextOverTheHubShould
{
    private static readonly string ShippedOrderType = typeof(ShippedOrderCommand).FullName!;

    private static TestWebHost CreateHost(params IDispatchContextEnricher[] enrichers)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddSingleton<IDispatchCultureResolver>(new DispatchPropertyCultureResolver(
                    "lang",
                    new Dictionary<string, CultureInfo>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["es"] = CultureInfo.GetCultureInfo("es-MX"),
                        ["en"] = CultureInfo.GetCultureInfo("en-US"),
                    }));
                builder.Services.AddSingleton<IRefusalTextRenderer, SpanishRefusalRenderer>();
                foreach (var enricher in enrichers)
                {
                    builder.Services.AddSingleton(enricher);
                }
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static async Task<string?> DispatchShippedOrderAsync(
        TestWebHost host, Dictionary<string, string>? headers)
    {
        await using var connection = new HubConnectionBuilder().WithUrl($"{host.Address}/conduit").Build();
        await connection.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new { });
        Dictionary<string, string>? noContext = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", ShippedOrderType, payload, noContext, headers);
        return result.Error?.Message;
    }

    [TestMethod]
    public async Task WriteTheRefusalInTheLanguageTheHostsEnricherSets()
    {
        // Arrange
        await using var host = CreateHost(new StaticValueEnricher("lang", "es"));

        // Act
        var message = await DispatchShippedOrderAsync(host, headers: null);

        // Assert
        message.Should().Be("es:orders.shipped");
    }

    [TestMethod]
    public async Task LetTheServerValueWinOverTheClientsHeader()
    {
        // Arrange
        await using var host = CreateHost(new StaticValueEnricher("lang", "en"));
        var clientHeaders = new Dictionary<string, string> { ["lang"] = "es" };

        // Act
        var message = await DispatchShippedOrderAsync(host, clientHeaders);

        // Assert
        message.Should().Be("Order was already shipped");
    }

    [TestMethod]
    public async Task KeepEnglishForAnUnknownClientLanguage()
    {
        // Arrange
        await using var host = CreateHost();
        var clientHeaders = new Dictionary<string, string> { ["lang"] = "xx" };

        // Act
        var message = await DispatchShippedOrderAsync(host, clientHeaders);

        // Assert
        message.Should().Be("Order was already shipped");
    }
}
