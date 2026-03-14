using System.Text.Json;
using AwesomeAssertions;
using Conduit.Dispatch;
using Conduit.TestUtils;
using Conduit.Transport.SignalR.Tests.TestApp;
using global::Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class ConduitHubTests
{
    private static TestHost CreateHost()
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitHandlers();
            },
            app => app.MapHub<ConduitHub>("/hub"));
    }

    private static async Task<HubConnection> ConnectToHub(string address)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{address}/hub")
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [TestMethod]
    public async Task Dispatch_Command_ReturnsOkResult()
    {
        // Arrange
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Name = "Hub Test" });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.TestApp.CreateItemCommand", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
    }

    [TestMethod]
    public async Task Dispatch_ConduitException_ReturnsFail()
    {
        // Arrange
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.TestApp.GetItemQuery", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("NotFound");
    }

    [TestMethod]
    public async Task Publish_Event_CompletesWithoutError()
    {
        // Arrange
        await using var host = CreateHost();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(
            new { Id = Guid.NewGuid(), Name = "Hub Event" });

        // Act
        await connection.InvokeAsync(
            "Publish", "Conduit.Transport.SignalR.Tests.TestApp.ItemCreatedEvent", payload);

        // Assert
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task Publish_ExceptionInHandler_SwallowedNoErrorToClient()
    {
        // Arrange -- the hub's Publish method catches all exceptions and logs them.
        // Publishing an unknown event type will cause the router to throw internally.
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { });

        // Act -- should complete without throwing HubException to the client
        var act = () => connection.InvokeAsync("Publish", "NonExistentEvent", payload);

        // Assert -- no exception propagated to the caller
        await act.Should().NotThrowAsync();
    }
}
