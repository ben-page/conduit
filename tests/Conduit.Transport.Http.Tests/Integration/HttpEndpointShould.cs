using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Transport.Http;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Runtime;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class HttpEndpointShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();

        var app = builder.Build();
        app.MapConduitEndpoint();
        app.MapHub<ConduitHub>("/conduit");
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
    public async Task ReturnErrorForUnknownType()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "NonExistent.NonExistentCommand",
                new { });

            // Assert
            ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        }
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
}
