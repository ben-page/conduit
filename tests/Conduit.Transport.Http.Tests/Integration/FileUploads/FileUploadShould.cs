using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Conduit.Transport.Http.Tests.Support;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Conduit.Transport.Http.Tests.Integration.FileUploads;

[TestClass]
public class FileUploadShould
{
    private static (WebApplication App, HttpClient Client, UploadedFileStore Store) CreateTestHost()
    {
        var builder = WebApplication.CreateBuilder();
        var store = new UploadedFileStore();
        builder.Services.AddSingleton(store);
        builder.Services.AddConduit();
        builder.Services.AddConduitSignalR();

        var app = builder.Build();

        app.MapConduitPost<UploadBinaryCommand>("/uploads/binary");
        app.MapConduitPost<UploadConduitFileCommand>("/uploads/document");

        app.Urls.Add("http://127.0.0.1:0");
        app.Start();

        var address = app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return (app, client, store);
    }

    [TestMethod]
    public async Task BindBinaryDataFromMultipartFilePart()
    {
        // Arrange
        var (app, client, store) = CreateTestHost();
        await using (app)
        using (client)
        {
            var imageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0xDE, 0xAD };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Reactor"), "ScreenName");

            var fileContent = new ByteArrayContent(imageBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fileContent, "Image", "logo.png");

            // Act
            var response = await client.PostAsync("/uploads/binary", form);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            store.ScreenName.Should().Be("Reactor");
            store.Image.Should().NotBeNull();
            store.Image!.ToArray().Should().Equal(imageBytes);
            store.Image.MediaType.Should().Be("image/png");
        }
    }

    [TestMethod]
    public async Task PopulateConduitFileFileNameFromContentDisposition()
    {
        // Arrange
        var (app, client, store) = CreateTestHost();
        await using (app)
        using (client)
        {
            var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0xCA, 0xFE, 0xBA, 0xBE };
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("Reactor"), "ScreenName");

            var fileContent = new ByteArrayContent(pdfBytes);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            form.Add(fileContent, "Document", "datasheet.pdf");

            // Act
            var response = await client.PostAsync("/uploads/document", form);

            // Assert
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            store.File.Should().NotBeNull();
            store.File!.FileName.Should().Be("datasheet.pdf");
            store.File.Content.MediaType.Should().Be("application/pdf");
            store.File.Content.ToArray().Should().Equal(pdfBytes);
        }
    }
}
