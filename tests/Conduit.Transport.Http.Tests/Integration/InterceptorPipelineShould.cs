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
public class InterceptorPipelineShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHostWithTypeFilter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddConduitTypeFilter(filter =>
        {
            filter.AllowType("Conduit.Transport.Http.Tests.Support.CreateItemCommand");
            filter.AllowType("Conduit.Transport.Http.Tests.Support.GetItemQuery");
        });

        var app = builder.Build();
        app.MapConduitEndpoint();
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
        var (app, client) = CreateTestHostWithTypeFilter();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Name = "Test" });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.CreateItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public async Task RejectDisallowedTypeWith401()
    {
        var (app, client) = CreateTestHostWithTypeFilter();
        await using (app)
        using (client)
        {
            var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });
            var envelope = new DispatchEnvelope("Conduit.Transport.Http.Tests.Support.DeleteItemCommand", payload);

            var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

            ((int)response.StatusCode).Should().Be(401);
        }
    }
}
