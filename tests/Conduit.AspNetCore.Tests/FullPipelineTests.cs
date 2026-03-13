using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Conduit.AspNetCore.Tests;

[TestClass]
[DoNotParallelize]
public class FullPipelineTests
{
    private static WebApplicationFactory<Program> factory = null!;
    private static HttpClient client = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext _)
    {
        factory = new WebApplicationFactory<Program>();
        client = factory.CreateClient();
    }

    [ClassCleanup]
    public static void ClassCleanup()
    {
        client.Dispose();
        factory.Dispose();
    }

    private static async Task<string> CreateItem(string name)
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
        var payload = JsonSerializer.SerializeToElement(new { Name = "Test Item" });
        var envelope = new DispatchEnvelope("CreateItemCommand", payload);

        var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data").GetString();
        data.Should().NotBeNullOrEmpty();
        Guid.TryParse(data, out _).Should().BeTrue();
    }

    [TestMethod]
    public async Task HttpDispatch_VoidCommand_ReturnsNullData()
    {
        var id = await CreateItem("To Delete");

        var deletePayload = JsonSerializer.SerializeToElement(new { Id = id });
        var deleteEnvelope = new DispatchEnvelope("DeleteItemCommand", deletePayload);

        var response = await client.PostAsJsonAsync("/api/dispatch", deleteEnvelope);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        json.GetProperty("data").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [TestMethod]
    public async Task HttpDispatch_Query_ReturnsResultEnvelope()
    {
        var id = await CreateItem("Query Test");

        var queryPayload = JsonSerializer.SerializeToElement(new { Id = id });
        var queryEnvelope = new DispatchEnvelope("GetItemQuery", queryPayload);

        var response = await client.PostAsJsonAsync("/api/dispatch", queryEnvelope);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var data = json.GetProperty("data");
        data.GetProperty("id").GetString().Should().Be(id);
        data.GetProperty("name").GetString().Should().Be("Query Test");
    }

    [TestMethod]
    public async Task HttpDispatch_HandlerThrowsNotFoundException_ReturnsErrorEnvelope()
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

    [TestMethod]
    public async Task HttpDispatch_UnknownType_ReturnsError()
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        var envelope = new DispatchEnvelope("NonExistentCommand", payload);

        var response = await client.PostAsJsonAsync("/api/dispatch", envelope);

        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }
}
