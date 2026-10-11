using System.Net;
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
/// Proves a plain minimal-API route can opt into Conduit's inbound dispatch context with
/// <c>WithConduitDispatchContext()</c>, so an in-process <see cref="IMediator"/> dispatch from its handler
/// carries the server-trusted user id like the hub, the wire endpoint and a REST-mapped command.
/// </summary>
[TestClass]
public class MinimalApiDispatchContextShould
{
    private static (WebApplication App, HttpClient Client, CapturedProperties Captured) CreateTestHost(bool optIn)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<CapturedProperties>();
        builder.Services.AddConduit();
        builder.Services.AddSingleton<IDispatchContextEnricher, SubClaimUserIdEnricher>();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var identity = new ClaimsIdentity([new Claim("sub", "emp-42")], "test");
            context.User = new ClaimsPrincipal(identity);
            await next(context);
        });

        var route = app.MapPost("/api/minimal", async (IMediator mediator) =>
        {
            await mediator.ExecuteAsync(new CapturePropertiesCommand());
            return Results.Ok();
        });
        if (optIn)
        {
            route.WithConduitDispatchContext();
        }

        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client, app.Services.GetRequiredService<CapturedProperties>());
    }

    [TestMethod]
    public async Task ContributeTheEnrichedUserIdToAnInProcessDispatch_OnAnOptedInRoute()
    {
        var (app, client, captured) = CreateTestHost(optIn: true);
        await using (app)
        using (client)
        {
            var response = await client.PostAsync("/api/minimal", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Properties.Should().NotBeNull();
            captured.Properties!["UserId"].Should().Be("emp-42");
        }
    }

    [TestMethod]
    public async Task LeaveTheDispatchUnenriched_OnARouteThatDidNotOptIn()
    {
        var (app, client, captured) = CreateTestHost(optIn: false);
        await using (app)
        using (client)
        {
            var response = await client.PostAsync("/api/minimal", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (captured.Properties is null || !captured.Properties.ContainsKey("UserId")).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task IgnoreAClientSuppliedUserId()
    {
        var (app, client, captured) = CreateTestHost(optIn: true);
        await using (app)
        using (client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/minimal");
            request.Headers.Add("UserId", "attacker");
            request.Headers.Add("X-Conduit-Message-Id", "m-1");
            request.Headers.Add("X-Conduit-Conversation-Id", "c-1");
            request.Headers.Add("X-Conduit-Causation-Id", "p-1");

            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Properties.Should().NotBeNull();
            captured.Properties!["UserId"].Should().Be("emp-42");
        }
    }
}
