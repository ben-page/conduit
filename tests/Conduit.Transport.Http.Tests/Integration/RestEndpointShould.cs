using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        builder.Services.AddSingleton<UploadedFileStore>();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();

        var app = builder.Build();

        app.MapConduitGet<GetItemQuery>("/api/items/{id}");
        app.MapConduitPost<CreateItemCommand>("/api/items");
        app.MapConduitPost<SetItemStatusCommand>("/api/items/status");
        app.MapConduitDelete<DeleteItemCommand>("/api/items/{id}");
        app.MapConduitPost<TakeItemCommand>("/api/items/take");
        app.MapConduitPost<AttachDocumentCommand>("/api/documents");
        app.MapConduitPost<UploadConduitFileCommand>("/api/uploads/document");

        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    [TestMethod]
    public async Task ReadAnEnumByName()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var content = new StringContent("{\"status\":\"Open\"}", System.Text.Encoding.UTF8, "application/json");

            // Act
            var response = await client.PostAsync("/api/items/status", content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<string>()).Should().Be("Open");
        }
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

    [TestMethod]
    public async Task Answer400ForMalformedJson()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var content = new StringContent("{\"name\":", Encoding.UTF8, "application/json");

            // Act
            var response = await client.PostAsync("/api/items", content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("title").GetString().Should().Be("BadRequest");
            json.GetProperty("status").GetInt32().Should().Be(400);
            json.GetProperty("detail").GetString().Should().NotBeNullOrEmpty();
        }
    }

    [TestMethod]
    public async Task Answer415ForANonJsonBody()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var content = new StringContent("name=Widget", Encoding.UTF8, "text/plain");

            // Act
            var response = await client.PostAsync("/api/items", content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        }
    }

    [TestMethod]
    public async Task Answer400ForANullBody()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var content = new StringContent("null", Encoding.UTF8, "application/json");

            // Act
            var response = await client.PostAsync("/api/items", content);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("title").GetString().Should().Be("BadRequest");
        }
    }

    [TestMethod]
    public async Task Answer400ForABadEnumInMultipart()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Bogus"), "Status");

            // Act
            var response = await client.PostAsync("/api/items/status", form);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("title").GetString().Should().Be("BadRequest");
            json.GetProperty("detail").GetString().Should().Contain("Bogus");
        }
    }

    [TestMethod]
    public async Task ReadAnEnumFromMultipartIgnoringCase()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("active"), "Status");

            // Act
            var response = await client.PostAsync("/api/items/status", form);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<string>()).Should().Be("Active");
        }
    }

    [TestMethod]
    public async Task BindSettablePropertiesOfAClassCommandFromMultipart()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Datasheet"), "title");
            form.Add(new StringContent("Inactive"), "Status");
            var fileContent = new ByteArrayContent([0x25, 0x50, 0x44, 0x46]);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(fileContent, "Document", "datasheet.pdf");

            // Act
            var response = await client.PostAsync("/api/documents", form);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<string>()).Should().Be("Datasheet|Inactive|datasheet.pdf|4");
        }
    }

    [TestMethod]
    public async Task Answer400ForAMissingRequiredFile()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Reactor"), "ScreenName");

            // Act
            var response = await client.PostAsync("/api/uploads/document", form);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            json.GetProperty("title").GetString().Should().Be("BadRequest");
            json.GetProperty("detail").GetString().Should().Contain("Document");
        }
    }

    [TestMethod]
    public async Task Answer204ForANullCommandResult()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.PostAsJsonAsync("/api/items/take", new { Id = Guid.NewGuid() });

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }
    }
}
