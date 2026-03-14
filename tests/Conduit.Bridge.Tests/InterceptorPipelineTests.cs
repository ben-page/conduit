using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Bridge.Http;
using Conduit.Bridge.SignalR;
using Conduit.Bridge.Tests.TestApp;
using Conduit.Transport.SignalR;
using Conduit.Dispatch;
using global::Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Bridge.Tests;

[TestClass]
public class InterceptorPipelineTests
{
    private static (WebApplication App, HttpClient Client) CreateTestHostWithBridge()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitBridge(bridge =>
        {
            bridge.AllowType("CreateItemCommand");
            bridge.AllowType("GetItemQuery");
        });
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

    [TestMethod]
    public async Task BridgeInterceptor_AllowedType_Succeeds()
    {
        var (app, client) = CreateTestHostWithBridge();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Name = "Test" });
            var envelope = new DispatchEnvelope("CreateItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public async Task BridgeInterceptor_DisallowedType_ReturnsError()
    {
        var (app, client) = CreateTestHostWithBridge();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });
            var envelope = new DispatchEnvelope("DeleteItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            ((int)response.StatusCode).Should().Be(401);
        }
    }
}
