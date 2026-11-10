using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration;

/// <summary>
/// Proves that per-command REST endpoints run dispatch-context enrichers, so server-trusted
/// attribution (e.g. the authenticated user id on <c>MessageContext.Properties</c>) is identical to
/// the SignalR hub and the wire endpoint. Without this, a command mapped to a REST route would
/// dispatch with an empty property bag and silently lose its user attribution.
/// </summary>
[TestClass]
public class RestDispatchEnricherShould
{
    private static (WebApplication App, HttpClient Client, CapturedProperties Captured) CreateTestHost(
        bool withEnricher, string? authenticatedSub)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<CapturedProperties>();
        builder.Services.AddConduit();
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

        app.MapConduitPost<CapturePropertiesCommand>("/api/capture");
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        var captured = app.Services.GetRequiredService<CapturedProperties>();
        return (app, client, captured);
    }

    private static async Task PostCaptureAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/capture", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [TestMethod]
    public async Task ContributeServerTrustedUserIdOnRestMappedCommand()
    {
        // Arrange
        var (app, client, captured) = CreateTestHost(withEnricher: true, authenticatedSub: "emp-42");
        await using (app)
        using (client)
        {
            // Act
            await PostCaptureAsync(client);

            // Assert — the REST-mapped command carries the same server-trusted UserId as the hub/wire.
            captured.Properties.Should().NotBeNull();
            captured.Properties!["UserId"].Should().Be("emp-42");
        }
    }

    [TestMethod]
    public async Task NotEnrichWhenNoEnricherRegistered()
    {
        // Arrange — backward compatible: no enricher, dispatch starts with an empty property bag.
        var (app, client, captured) = CreateTestHost(withEnricher: false, authenticatedSub: "emp-42");
        await using (app)
        using (client)
        {
            // Act
            await PostCaptureAsync(client);

            // Assert
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
            await PostCaptureAsync(client);

            // Assert
            (captured.Properties is null || !captured.Properties.ContainsKey("UserId"))
                .Should().BeTrue();
        }
    }
}
