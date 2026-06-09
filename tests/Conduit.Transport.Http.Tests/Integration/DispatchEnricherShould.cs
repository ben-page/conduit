using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class DispatchEnricherShould
{
    private const string CaptureType = "Conduit.Transport.Http.Tests.Support.CapturePropertiesCommand";

    private static (WebApplication App, HttpClient Client, CapturedProperties Captured) CreateTestHost(
        bool withEnricher, string? authenticatedSub)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<CapturedProperties>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        if (withEnricher)
        {
            builder.Services.AddSingleton<IDispatchContextEnricher, SubClaimUserIdEnricher>();
        }

        var app = builder.Build();

        // Inject a fixed authenticated principal so the enricher sees HttpContext.User.
        if (authenticatedSub is not null)
        {
            app.Use(async (context, next) =>
            {
                var identity = new ClaimsIdentity([new Claim("sub", authenticatedSub)], "test");
                context.User = new ClaimsPrincipal(identity);
                await next(context);
            });
        }

        app.MapConduitEndpoint();
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var captured = app.Services.GetRequiredService<CapturedProperties>();
        return (app, client, captured);
    }

    private static async Task DispatchCaptureAsync(HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/conduit/dispatch")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { }, ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", CaptureType);
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ContributeServerTrustedUserIdFromAuthenticatedPrincipal()
    {
        // Arrange
        var (app, client, captured) = CreateTestHost(withEnricher: true, authenticatedSub: "emp-42");
        await using (app)
        using (client)
        {
            // Act
            await DispatchCaptureAsync(client);

            // Assert
            captured.Properties.Should().NotBeNull();
            captured.Properties!["UserId"].Should().Be("emp-42");
        }
    }

    [TestMethod]
    public async Task NotEnrichWhenNoEnricherRegistered()
    {
        // Arrange — backward compatible: no enricher, no properties contributed.
        var (app, client, captured) = CreateTestHost(withEnricher: false, authenticatedSub: "emp-42");
        await using (app)
        using (client)
        {
            // Act
            await DispatchCaptureAsync(client);

            // Assert — context starts empty (no correlation headers, no enrichers).
            (captured.Properties is null || captured.Properties.Count == 0).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task NotContributeUserIdForUnauthenticatedRequest()
    {
        // Arrange — enricher registered but no authenticated principal: it no-ops.
        var (app, client, captured) = CreateTestHost(withEnricher: true, authenticatedSub: null);
        await using (app)
        using (client)
        {
            // Act
            await DispatchCaptureAsync(client);

            // Assert
            (captured.Properties is null || !captured.Properties.ContainsKey("UserId"))
                .Should().BeTrue();
        }
    }
}
