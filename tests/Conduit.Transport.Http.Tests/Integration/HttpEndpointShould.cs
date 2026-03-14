using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Transport.Http;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Dispatch;
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
        app.MapHub<ConduitHub>("/hub");
        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    private static async Task<string> CreateItem(HttpClient client, string name)
    {
        var payload = JsonSerializer.SerializeToElement(new { Name = name });
        var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.CreateItemCommand", payload);
        var response = await client.PostAsJsonAsync("/api/dispatch", envelope);
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
            var payload = JsonSerializer.SerializeToElement(new { Name = "Test Item" });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.CreateItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

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

            var deletePayload = JsonSerializer.SerializeToElement(new { Id = id });
            var deleteEnvelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.DeleteItemCommand", deletePayload);

            var response = await client.PostAsJsonAsync("/api/dispatch", deleteEnvelope);

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

            var queryPayload = JsonSerializer.SerializeToElement(new { Id = id });
            var queryEnvelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.GetItemQuery", queryPayload);

            var response = await client.PostAsJsonAsync("/api/dispatch", queryEnvelope);

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
            var queryPayload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });
            var queryEnvelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.GetItemQuery", queryPayload);

            var response = await client.PostAsJsonAsync("/api/dispatch", queryEnvelope);

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
            var payload = JsonSerializer.SerializeToElement(new { });
            var envelope = new DispatchEnvelope("NonExistent.NonExistentCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

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
            var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid(), Name = "Event" });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.ItemCreatedEvent", payload);

            // Act
            var response = await client.PostAsJsonAsync("/api/dispatch/publish", envelope);

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
            var payload = JsonSerializer.SerializeToElement(new { });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.ThrowConflictCommand", payload);

            // Act
            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("Conflict");
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
            var payload = JsonSerializer.SerializeToElement(new { });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.ThrowUnauthorizedCommand", payload);

            // Act
            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

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
            var payload = JsonSerializer.SerializeToElement(new { });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.ThrowGenericCommand", payload);

            // Act
            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            // Assert
            ((int)response.StatusCode).Should().Be(500);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("error").GetProperty("code").GetString().Should().Be("InternalError");
        }
    }
}
