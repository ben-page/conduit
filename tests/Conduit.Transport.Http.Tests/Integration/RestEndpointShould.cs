using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration;

[TestClass]
public class RestEndpointShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ItemStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();

        var app = builder.Build();

        app.MapConduitGet<GetItemQuery>("/api/items/{id}");
        app.MapConduitPost<CreateItemCommand>("/api/items");
        app.MapConduitDelete<DeleteItemCommand>("/api/items/{id}");

        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    [TestMethod]
    public async Task ReturnQueryResultForGet()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var createResponse = await client.PostAsJsonAsync("/api/items", new { Name = "Test Item" });
            var id = await createResponse.Content.ReadFromJsonAsync<Guid>();

            // Act
            var response = await client.GetAsync($"/api/items/{id}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var name = await response.Content.ReadFromJsonAsync<string>();
            name.Should().Be("Test Item");
        }
    }

    [TestMethod]
    public async Task ReturnResultForCommandPost()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.PostAsJsonAsync("/api/items", new { Name = "Created" });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var id = await response.Content.ReadFromJsonAsync<Guid>();
            Guid.TryParse(id.ToString(), out _).Should().BeTrue();
        }
    }

    [TestMethod]
    public async Task Return204ForVoidCommandDelete()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var createResponse = await client.PostAsJsonAsync("/api/items", new { Name = "To Delete" });
            var id = await createResponse.Content.ReadFromJsonAsync<Guid>();

            // Act
            var response = await client.DeleteAsync($"/api/items/{id}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }

    [TestMethod]
    public async Task Return404ProblemDetailsForNotFoundException()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.GetAsync($"/api/items/{Guid.NewGuid()}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("title").GetString().Should().Be("NotFound");
            json.GetProperty("status").GetInt32().Should().Be(404);
            json.GetProperty("detail").GetString().Should().Contain("not found");
        }
    }
}
