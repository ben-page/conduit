using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Bridge.Http;
using Conduit.Bridge.SignalR.Tests.Support;
using Conduit.Transport.SignalR;
using Microsoft.Extensions.Hosting;
using Conduit.Dispatch;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Bridge.SignalR.Tests.Integration;

[TestClass]
public class InterceptorPipelineShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHostWithBridge()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitBridge(bridge =>
        {
            bridge.AllowType("Conduit.Bridge.SignalR.Tests.Support.CreateItemCommand");
            bridge.AllowType("Conduit.Bridge.SignalR.Tests.Support.GetItemQuery");
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
    public async Task DispatchAllowedType()
    {
        var (app, client) = CreateTestHostWithBridge();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Name = "Test" });
            var envelope = new DispatchEnvelope("Conduit.Bridge.SignalR.Tests.Support.CreateItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public async Task RejectDisallowedTypeWith401()
    {
        var (app, client) = CreateTestHostWithBridge();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });
            var envelope = new DispatchEnvelope("Conduit.Bridge.SignalR.Tests.Support.DeleteItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            ((int)response.StatusCode).Should().Be(401);
        }
    }
}
