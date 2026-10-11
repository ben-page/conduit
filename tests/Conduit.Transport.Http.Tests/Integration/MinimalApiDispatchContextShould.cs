using System.Net;
using System.Security.Claims;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
    private static (WebApplication App, HttpClient Client, CapturedProperties Captured) CreateTestHost(bool optIn, Action<WebApplication>? mapMore = null)
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

        mapMore?.Invoke(app);

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

    private static bool IsTagged(WebApplication app, string pattern) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern)
            .Metadata.GetMetadata<ConduitDispatchContextMetadata>() is not null;

    [TestMethod]
    public async Task TagTheEndpoint_OnAnOptedInRoute()
    {
        var (app, client, _) = CreateTestHost(
            optIn: true,
            mapMore: a => a.MapPost("/api/plain", () => Results.Ok()));
        await using (app)
        using (client)
        {
            IsTagged(app, "/api/minimal").Should().BeTrue();
            IsTagged(app, "/api/plain").Should().BeFalse();
        }
    }

    [TestMethod]
    public async Task OptEveryRouteIn_WhenAppliedToARouteGroup()
    {
        var (app, client, captured) = CreateTestHost(
            optIn: false,
            mapMore: a =>
            {
                var g = a.MapGroup("").WithConduitDispatchContext();
                g.MapPost("/api/grouped", async (IMediator mediator) =>
                {
                    await mediator.ExecuteAsync(new CapturePropertiesCommand());
                    return Results.Ok();
                });
                g.MapGroup("/inner").MapPost("/nested", async (IMediator mediator) =>
                {
                    await mediator.ExecuteAsync(new CapturePropertiesCommand());
                    return Results.Ok();
                });
            });
        await using (app)
        using (client)
        {
            IsTagged(app, "/api/grouped").Should().BeTrue();
            IsTagged(app, "/inner/nested").Should().BeTrue();
            IsTagged(app, "/api/minimal").Should().BeFalse();

            foreach (var path in new[] { "/api/grouped", "/inner/nested" })
            {
                captured.Properties = null;
                var response = await client.PostAsync(path, null);

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                captured.Properties.Should().NotBeNull();
                captured.Properties!["UserId"].Should().Be("emp-42");
            }
        }
    }

    [TestMethod]
    public async Task RunTheHandler_WhenTheResponseIsAlready400_OnAnOptedInRoute()
    {
        // The OpenIddict error-passthrough shape: a middleware pre-sets 400 and relies on the handler to override it.
        // A route handler wrapped by an endpoint filter is skipped in that state; a wrapped request delegate is not.
        var (app, client, captured) = CreateTestHost(
            optIn: false,
            mapMore: a =>
            {
                a.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/callback")
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    }

                    await next(context);
                });
                a.MapPost("/callback", async (IMediator mediator) =>
                {
                    await mediator.ExecuteAsync(new CapturePropertiesCommand());
                    return Results.Redirect("/next");
                }).WithConduitDispatchContext();
            });
        await using (app)
        using (client)
        {
            using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CheckCertificateRevocationList = true })
            {
                BaseAddress = client.BaseAddress,
            };

            var response = await noRedirect.PostAsync("/callback", null);

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            captured.Properties.Should().NotBeNull();
            captured.Properties!["UserId"].Should().Be("emp-42");
        }
    }

    [TestMethod]
    public async Task NotLeakTheStartedContextToMiddlewareAfterTheEndpoint_OnAnOptedInRoute()
    {
        MessageContext? before = null;
        MessageContext? after = null;
        var (app, client, captured) = CreateTestHost(
            optIn: false,
            mapMore: a =>
            {
                a.Use(async (context, next) =>
                {
                    if (context.Request.Path == "/leak")
                    {
                        before = MessageContext.Current;
                        await next(context);
                        after = MessageContext.Current;
                        return;
                    }

                    await next(context);
                });
                a.MapPost("/leak", async (IMediator mediator) =>
                {
                    await mediator.ExecuteAsync(new CapturePropertiesCommand());
                    return Results.Ok();
                }).WithConduitDispatchContext();
            });
        await using (app)
        using (client)
        {
            var response = await client.PostAsync("/leak", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            captured.Properties.Should().NotBeNull();
            captured.Properties!["UserId"].Should().Be("emp-42");
            before.Should().BeNull();
            after.Should().BeNull("the started context must not flow out of the wrapped request delegate");
        }
    }
}
