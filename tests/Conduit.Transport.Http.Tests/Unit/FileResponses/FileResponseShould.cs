using System.Net;
using System.Net.Http.Json;
using AwesomeAssertions;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Unit.FileResponses;

[TestClass]
public class FileResponseShould
{
    private static (WebApplication App, HttpClient Client) CreateTestHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();
        builder.Services.AddSingleton<ItemStore>();

        var app = builder.Build();

        app.MapConduitGet<GetPngImageQuery>("/files/png/{Name}");
        app.MapConduitGet<GetOctetStreamQuery>("/files/octet/{Name}");
        app.MapConduitGet<GetExportFileQuery>("/files/export/{Range}");
        app.MapConduitGet<GetMissingFileQuery>("/files/missing/{Name}");
        app.MapConduitGet<GetItemQuery>("/api/items/{id}");
        app.MapConduitPost<CreateItemCommand>("/api/items");

        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client);
    }

    [TestMethod]
    public async Task ReturnBinaryDataWithDeclaredMediaType()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.GetAsync("/files/png/logo");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            var body = await response.Content.ReadAsByteArrayAsync();
            body.Should().Equal(GetPngImageHandler.PngBytes);
        }
    }

    [TestMethod]
    public async Task FallBackToOctetStreamWhenBinaryDataMediaTypeIsNull()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.GetAsync("/files/octet/blob");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            var body = await response.Content.ReadAsByteArrayAsync();
            body.Should().Equal(GetOctetStreamHandler.Bytes);
        }
    }

    [TestMethod]
    public async Task EmitContentDispositionWhenConduitFileHasFileName()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.GetAsync("/files/export/Q1");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            response.Content.Headers.ContentDisposition.Should().NotBeNull();
            response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            response.Content.Headers.ContentDisposition.FileName.Should().Be(GetExportFileHandler.FileName);

            var body = await response.Content.ReadAsStringAsync();
            body.Should().Be(GetExportFileHandler.Csv);
        }
    }

    [TestMethod]
    public async Task ReturnNotFoundWhenNullableFileQueryReturnsNull()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            // Act
            var response = await client.GetAsync("/files/missing/nothing");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [TestMethod]
    public async Task ReturnJsonForPlainDtoQuery()
    {
        // Arrange
        var (app, client) = CreateTestHost();
        await using (app)
        using (client)
        {
            var createResponse = await client.PostAsJsonAsync("/api/items", new { Name = "Regression" });
            var id = await createResponse.Content.ReadFromJsonAsync<Guid>();

            // Act
            var response = await client.GetAsync($"/api/items/{id}");

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
            var name = await response.Content.ReadFromJsonAsync<string>();
            name.Should().Be("Regression");
        }
    }
}
