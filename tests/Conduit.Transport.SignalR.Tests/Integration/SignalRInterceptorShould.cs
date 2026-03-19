using System.Text.Json;
using AwesomeAssertions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.SignalR;
using Conduit.Generated;
using Conduit.Transport.SignalR.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.SignalR.Tests.Integration;

[TestClass]
public class SignalRInterceptorShould
{
    private static TestWebHost CreateHostWithTypeFilter()
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<ItemStore>();
                builder.Services.AddConduit(conduit =>
                {
                    conduit.AddTypeFilter(filter =>
                    {
                        filter.AllowType("Conduit.Transport.SignalR.Tests.Support.CreateItemCommand");
                        filter.AllowType("Conduit.Transport.SignalR.Tests.Support.GetItemQuery");
                    });
                });
                builder.Services.AddConduitSignalR();
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
    public async Task DispatchAllowedType()
    {
        // Arrange
        await using var host = CreateHostWithTypeFilter();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Name = "Allowed" });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.CreateItemCommand", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public async Task RejectDisallowedType()
    {
        // Arrange
        await using var host = CreateHostWithTypeFilter();
        await using var connection = await ConnectToHub(host.Address);
        var payload = JsonSerializer.SerializeToElement(new { Id = Guid.NewGuid() });

        // Act
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", "Conduit.Transport.SignalR.Tests.Support.DeleteItemCommand", payload);

        // Assert
        result.Should().NotBeNull();
        result.Error.Should().NotBeNull();
        result.Error.Code.Should().Be("Unauthorized");
    }
}
