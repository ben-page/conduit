using System.Text.Json;
using AwesomeAssertions;
using Conduit.Bridge.SignalR;
using Conduit.Bridge.SignalR.Tests.TestApp;
using Conduit.Dispatch;
using Conduit.TestUtils;
using Conduit.Transport.SignalR;
using global::Conduit.Generated;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Bridge.SignalR.Tests.Integration;

[TestClass]
public class SignalRBridgeInterceptorTests
{
    private static TestHost CreateHostWithBridge()
    {
        return TestHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                builder.Services.AddConduitBridge(bridge =>
                {
                    bridge.AllowType("Conduit.Bridge.SignalR.Tests.TestApp.CreateItemCommand");
                    bridge.AllowType("Conduit.Bridge.SignalR.Tests.TestApp.GetItemQuery");
                });
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
    public async Task AllowedType_Dispatches_ReturnsOk()
    {
        // Arrange
        await using var host = CreateHostWithBridge();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Name = "Allowed" });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Bridge.SignalR.Tests.TestApp.CreateItemCommand", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public async Task DisallowedType_Returns_UnauthorizedError()
    {
        // Arrange
        await using var host = CreateHostWithBridge();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Bridge.SignalR.Tests.TestApp.DeleteItemCommand", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("Unauthorized");
    }
}
