using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Generated;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class ConduitHubShould
{
    private static TestWebHost CreateHost()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    private static async Task<HubConnection> ConnectToHub(string address)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{address}/conduit")
            .Build();
        await connection.StartAsync();
        return connection;
    }

    [TestMethod]
    public async Task ReturnOkResultForCommand()
    {
        // Arrange
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Name = "Hub Test" });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.CreateItemCommand", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().BeNull();
        result.Data.Should().NotBeNull();
    }

    [TestMethod]
    public async Task ReturnFailForConduitException()
    {
        // Arrange
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.GetItemQuery", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().NotBeNull();
        result.Error.Code.Should().Be("NotFound");
    }

    [TestMethod]
    public async Task DeliverPublishedEventToListener()
    {
        // Arrange
        await using var host = CreateHost();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(
            new { Id = Guid.NewGuid(), Name = "Hub Event" });

        // Act
        await connection.InvokeAsync(
            "Publish", "Conduit.Transport.SignalR.Tests.Support.ItemCreatedEvent", payload);

        // Assert
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task PropagateListenerFailureAsHubError()
    {
        // Arrange — ExplodingEventListener throws InvalidOperationException.
        // The wire event dispatch now propagates EventListenerException so
        // transports (service bus, SignalR) can decide how to handle the failure.
        await using var host = CreateHost();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Data = "boom" });

        // Act — publish an event whose listener throws
        var act = () => connection.InvokeAsync(
            "Publish",
            "Conduit.Transport.SignalR.Tests.Support.ExplodingEvent",
            payload);

        // Assert — exception propagates to the caller
        await act.Should().ThrowAsync<Exception>();
    }
}
