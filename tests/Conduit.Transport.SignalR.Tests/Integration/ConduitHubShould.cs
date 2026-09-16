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
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.CreateItemCommand", payload, noContext, noHeaders);

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
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.GetItemQuery", payload, noContext, noHeaders);

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
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        await connection.InvokeAsync(
            "Publish", "Conduit.Transport.SignalR.Tests.Support.ItemCreatedEvent", payload, noContext, noHeaders);

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
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        var act = () => connection.InvokeAsync(
            "Publish",
            "Conduit.Transport.SignalR.Tests.Support.ExplodingEvent",
            payload,
            noContext,
            noHeaders);

        // Assert — exception propagates to the caller
        await act.Should().ThrowAsync<Exception>();
    }

    private static TestWebHost CreateHost(ConduitServerInfo serverInfo)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR(serverInfo);
            },
            app => app.MapHub<ConduitHub>("/conduit"));
    }

    // Sent on connect rather than broadcast on restart: a restart broadcast reaches the clients connected to that pod,
    // which at startup is nobody, and events do not replay. Every client that needs the id necessarily connects.
    [TestMethod]
    public async Task SendTheRegisteredServerInfoToACallerOnConnect()
    {
        // Arrange
        await using var host = CreateHost(new ConduitServerInfo("build-123"));
        var received = new TaskCompletionSource<ConduitServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.Address}/conduit")
            .Build();
        connection.On<ConduitServerInfo>(ConduitHub.ServerInfoMethod, info => received.TrySetResult(info));

        // Act
        await connection.StartAsync();

        // Assert
        var info = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        info.Should().Be(new ConduitServerInfo("build-123"));
    }

    // Carried, never derived: a host that registers no ConduitServerInfo sends nothing.
    [TestMethod]
    public async Task SendNoServerInfoWhenTheHostRegistersNone()
    {
        // Arrange
        await using var host = CreateHost();
        var received = new TaskCompletionSource<ConduitServerInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl($"{host.Address}/conduit")
            .Build();
        connection.On<ConduitServerInfo>(ConduitHub.ServerInfoMethod, info => received.TrySetResult(info));
        await connection.StartAsync();
        var payload = JsonSerializer.SerializeToElement(new { Name = "After connect" });

        // Act: a round trip on the same connection. The server reads nothing from a connection until its
        // OnConnectedAsync has returned, and messages arrive in order, so anything it sent would already be here.
        Dictionary<string, string>? noContext = null;
        Dictionary<string, string>? noHeaders = null;
        await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.CreateItemCommand", payload, noContext, noHeaders);

        // Assert
        received.Task.IsCompleted.Should().BeFalse();
    }
}
