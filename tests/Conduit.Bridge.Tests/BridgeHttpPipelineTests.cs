using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Bridge.Http;
using Conduit.Bridge.Tests.TestApp;
using Conduit.Transport.SignalR;
using Conduit.Dispatch;
using global::Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Bridge.Tests;

[TestClass]
public class BridgeHttpPipelineTests
{
    private static (WebApplication App, HttpClient Client) CreateTestHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitHandlers();

        var app = builder.Build();
        app.MapConduitBridge();
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
        var envelope = new DispatchEnvelope("CreateItemCommand", payload);
        var response = await client.PostAsJsonAsync("/api/dispatch", envelope);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("data").GetString()!;
    }

    [TestMethod]
    public async Task HttpDispatch_CommandWithResult_ReturnsDataEnvelope()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Name = "Test Item" });
            var envelope = new DispatchEnvelope("CreateItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data").GetString();
            data.Should().NotBeNullOrEmpty();
            Guid.TryParse(data, out _).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task HttpDispatch_VoidCommand_ReturnsNullData()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var id = await CreateItem(client, "To Delete");

            var deletePayload = JsonSerializer.SerializeToElement(new { Id = id });
            var deleteEnvelope = new DispatchEnvelope("DeleteItemCommand", deletePayload);

            var response = await client.PostAsJsonAsync("/api/dispatch", deleteEnvelope);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [TestMethod]
    public async Task HttpDispatch_Query_ReturnsResultEnvelope()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var id = await CreateItem(client, "Query Test");

            var queryPayload = JsonSerializer.SerializeToElement(new { Id = id });
            var queryEnvelope = new DispatchEnvelope("GetItemQuery", queryPayload);

            var response = await client.PostAsJsonAsync("/api/dispatch", queryEnvelope);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var data = json.GetProperty("data");
            data.GetProperty("id").GetString().Should().Be(id);
            data.GetProperty("name").GetString().Should().Be("Query Test");
        }
    }

    [TestMethod]
    public async Task HttpDispatch_HandlerThrowsNotFoundException_ReturnsErrorEnvelope()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var queryPayload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });
            var queryEnvelope = new DispatchEnvelope("GetItemQuery", queryPayload);

            var response = await client.PostAsJsonAsync("/api/dispatch", queryEnvelope);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var error = json.GetProperty("error");
            error.GetProperty("code").GetString().Should().Be("NotFound");
            error.GetProperty("message").GetString().Should().Contain("not found");
        }
    }

    [TestMethod]
    public async Task HttpDispatch_UnknownType_ReturnsError()
    {
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { });
            var envelope = new DispatchEnvelope("NonExistentCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        }
    }
}
