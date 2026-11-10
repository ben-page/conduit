using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Transport.Http;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Runtime;
using Conduit.Generated;
using Conduit.TestUtils;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class HttpEndpointShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHost(SpyLoggerProvider? spy = null)
    {
        var builder = WebApplication.CreateBuilder();
        if (spy is not null)
        {
            builder.Logging.AddProvider(spy);
        }

        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();

        var app = builder.Build();
        app.MapConduitEndpoint();
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    private static async Task<HttpResponseMessage> PostConduitAsync(
        HttpClient client, string path, string type, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ConduitRuntime.JsonOptions),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Add("X-Conduit-Type", type);
        return await client.SendAsync(request);
    }

    private static async Task<string> CreateItem(HttpClient client, string name)
    {
        var response = await PostConduitAsync(
            client, "/api/conduit/dispatch",
            "Conduit.Transport.Http.Tests.Support.CreateItemCommand",
            new { Name = name });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetString()!;
    }

    [TestMethod]
    public async Task WriteEnumsAndClosedSetsAsTheGeneratedTypeScriptExpects()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.GetItemFacetsQuery",
                new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
            data.GetProperty("status").ValueKind.Should().Be(JsonValueKind.String);
            data.GetProperty("status").GetString().Should().Be("Active");
            data.GetProperty("reach").ValueKind.Should().Be(JsonValueKind.String);
            data.GetProperty("reach").GetString().Should().Be("self");
        }
    }

    [TestMethod]
    public async Task ReturnDataForCommandWithResult()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.CreateItemCommand",
                new { Name = "Test Item" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data").GetString();
            data.Should().NotBeNullOrEmpty();
            Guid.TryParse(data, out _).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task ReturnOkForVoidCommand()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var id = await CreateItem(client, "To Delete");

            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.DeleteItemCommand",
                new { Id = id });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public async Task ReturnResultForQuery()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var id = await CreateItem(client, "Query Test");

            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.GetItemQuery",
                new { Id = id });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data").GetString();
            data.Should().Be("Query Test");
        }
    }

    [TestMethod]
    public async Task Return404ForNotFoundException()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.GetItemQuery",
                new { Id = Guid.NewGuid() });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var error = json.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("NotFound");
            error.GetProperty("message").GetString().Should().Contain("not found");
        }
    }

    [TestMethod]
    public async Task AnswerUnknownTypeWith400AndNoStackTrace()
    {
        // Arrange -- a name the caller chose, which nothing declares or routes
        var spy = new SpyLoggerProvider();
        var (app, client) = CreateTestHost(spy);
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "NonExistent.NonExistentCommand",
                new { });

            // Assert -- a refusal (no support reference), logged once at Warning without an exception, never at Error
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("BadRequest");
            error.GetProperty("message").GetString().Should().Be("Unknown message type 'NonExistent.NonExistentCommand'.");
            var hasReference = error.TryGetProperty("reference", out var reference) && reference.ValueKind != JsonValueKind.Null;
            hasReference.Should().BeFalse("a refusal carries no support reference");
            spy.Entries.Should().ContainSingle(entry => entry.EventId.Name == "UnknownMessageType")
                .Which.Should().Match<SpyLogEntry>(entry => entry.Level == LogLevel.Warning && entry.Exception == null);
            spy.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        }
    }

    [TestMethod]
    public async Task AnswerUnknownEventTypeWith400()
    {
        // Arrange
        var spy = new SpyLoggerProvider();
        var (app, client) = CreateTestHost(spy);
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/publish",
                "NonExistent.NonExistentEvent",
                new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("BadRequest");
            error.GetProperty("message").GetString().Should().Be("Unknown message type 'NonExistent.NonExistentEvent'.");
            spy.Entries.Should().NotContain(entry => entry.Level >= LogLevel.Error);
        }
    }

    // A behavior that throws fails the event outside its listeners: the publisher hears what /dispatch answers an
    // unexpected failure -- InternalError, the fixed message, and the support reference the Error line carries
    [TestMethod]
    public async Task AnswerAnEventWhoseBehaviorThrowsWithAnInternalErrorAndAReference()
    {
        // Arrange
        var spy = new SpyLoggerProvider();
        await using var host = TestWebHost.Start(
            builder =>
            {
                builder.Logging.AddProvider(spy);
                builder.Services.AddConduit(conduit => conduit.AddPipeline(typeof(ThrowingEventBehavior<,>)));
            },
            app => app.MapConduitEndpoint());
        using var client = new HttpClient { BaseAddress = new Uri(host.Address) };

        // Act
        var response = await PostConduitAsync(
            client, "/api/conduit/publish", typeof(BehaviorFailureEvent).FullName!, new { Data = "x" });

        // Assert -- the InternalError envelope, status-coded as /dispatch codes it, its reference the logged one's
        ((int)response.StatusCode).Should().Be(500);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("InternalError");
        error.GetProperty("message").GetString().Should().Be("An unexpected error occurred.");
        var reference = error.GetProperty("reference").GetString();
        reference.Should().NotBeNullOrEmpty();
        var failure = spy.Entries.Should().ContainSingle(entry => entry.Level >= LogLevel.Error).Subject;
        failure.EventId.Id.Should().Be(2);
        failure.Message.Should().Contain(reference);
        failure.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("The behavior failed.");
    }

    [TestMethod]
    public async Task Return200ForPublishEndpoint()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/publish",
                "Conduit.Transport.Http.Tests.Support.ItemCreatedEvent",
                new { Id = Guid.NewGuid(), Name = "Event" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public async Task Return409ForConflictException()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.ThrowConflictCommand",
                new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("Conflict");
        }
    }

    [TestMethod]
    public async Task Return412ForPreconditionFailedException()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.ThrowPreconditionFailedCommand",
                new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("PreconditionFailed");
        }
    }

    [TestMethod]
    public async Task Return401ForUnauthorizedException()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.ThrowUnauthorizedCommand",
                new { });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("Unauthorized");
        }
    }

    [TestMethod]
    public async Task Return500ForUnhandledException()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.ThrowGenericCommand",
                new { });

            // Assert
            ((int)response.StatusCode).Should().Be(500);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("InternalError");
        }
    }

    // MapConduitEndpoint returns the route group both routes are mapped in, so a convention chained on it -- e.g.
    // app.MapConduitEndpoint().RequireAuthorization() -- reaches /dispatch and /publish alike.
    [TestMethod]
    public async Task ApplyAConventionChainedOnTheReturnedGroupToBothRoutes()
    {
        // Arrange
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddConduit();
        await using var app = builder.Build();
        var marker = new object();

        // Act
        app.MapConduitEndpoint("/internal/conduit").WithMetadata(marker);

        // Assert
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToDictionary(endpoint => endpoint.RoutePattern.RawText!, endpoint => endpoint.Metadata);
        endpoints.Keys.Should().BeEquivalentTo("/internal/conduit/dispatch", "/internal/conduit/publish");
        endpoints.Values.Should().AllSatisfy(metadata => metadata.Should().Contain(marker));
    }
}
