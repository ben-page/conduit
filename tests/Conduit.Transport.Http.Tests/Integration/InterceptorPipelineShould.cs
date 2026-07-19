using System.Net;
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
public class InterceptorPipelineShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHostWithTypeFilter()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit(conduit =>
        {
            conduit.AddTypeFilter(filter =>
            {
                filter.AllowType("Conduit.Transport.Http.Tests.Support.CreateItemCommand");
                filter.AllowType("Conduit.Transport.Http.Tests.Support.GetItemQuery");
            });
        });
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

    [TestMethod]
    public async Task DispatchAllowedType()
    {
        var (app, client) = CreateTestHostWithTypeFilter();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.CreateItemCommand",
                new { Name = "Test" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [TestMethod]
    public async Task RejectDisallowedTypeWith403()
    {
        var (app, client) = CreateTestHostWithTypeFilter();
        await using (app)
        using (client)
        {
            // Act
            var response = await PostConduitAsync(
                client, "/api/conduit/dispatch",
                "Conduit.Transport.Http.Tests.Support.DeleteItemCommand",
                new { Id = Guid.NewGuid() });

            // Assert
            ((int)response.StatusCode).Should().Be(403);
        }
    }
}
