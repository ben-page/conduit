using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.Transport.Http.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class RefusalTextOverHttpShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHost(bool withLanguage)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddConduit();
        builder.Services.AddSingleton<IDispatchCultureResolver>(new DispatchPropertyCultureResolver(
            "lang",
            new Dictionary<string, CultureInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["es"] = CultureInfo.GetCultureInfo("es-MX"),
                ["en"] = CultureInfo.GetCultureInfo("en-US"),
            }));
        builder.Services.AddSingleton<IRefusalTextRenderer, SpanishRefusalRenderer>();
        if (withLanguage)
        {
            builder.Services.AddSingleton<IDispatchContextEnricher>(new StaticValueEnricher("lang", "es"));
        }

        var app = builder.Build();
        app.MapConduitEndpoint();
        app.MapConduitPost<ShippedOrderCommand>("/api/orders/ship");
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }

    [TestMethod]
    public async Task WriteTheEnvelopeRefusalInTheReadersLanguage()
    {
        // Arrange
        var (app, client) = CreateTestHost(withLanguage: true);
        await using (app)
        using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conduit/dispatch")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-Conduit-Type", typeof(ShippedOrderCommand).FullName);

            // Act
            var response = await client.SendAsync(request);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("message").GetString().Should().Be("es:orders.shipped");
        }
    }

    [TestMethod]
    public async Task WriteTheRestProblemDetailInTheReadersLanguage()
    {
        // Arrange
        var (app, client) = CreateTestHost(withLanguage: true);
        await using (app)
        using (client)
        {
            // Act
            var response = await client.PostAsJsonAsync("/api/orders/ship", new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("detail").GetString().Should().Be("es:orders.shipped");
            problem.GetProperty("title").GetString().Should().Be("Conflict");
            problem.GetProperty("status").GetInt32().Should().Be(409);
        }
    }

    [TestMethod]
    public async Task KeepTheRestProblemDetailInEnglishWhenNothingSetsALanguage()
    {
        // Arrange — no enricher and no correlation headers: there is no MessageContext at the catch.
        var (app, client) = CreateTestHost(withLanguage: false);
        await using (app)
        using (client)
        {
            // Act
            var response = await client.PostAsJsonAsync("/api/orders/ship", new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("detail").GetString().Should().Be("Order was already shipped");
        }
    }
}
