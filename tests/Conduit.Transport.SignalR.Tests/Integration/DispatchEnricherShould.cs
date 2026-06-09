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
public class DispatchEnricherShould
{
    private const string CaptureType = "Conduit.Transport.SignalR.Tests.Support.CapturePropertiesCommand";

    private static TestWebHost CreateHost(params IDispatchContextEnricher[] enrichers)
    {
        return TestWebHost.Start(
            builder =>
            {
                builder.Services.AddSingleton<CapturedProperties>();
                builder.Services.AddConduit();
                builder.Services.AddConduitSignalR();
                foreach (var enricher in enrichers)
                {
                    builder.Services.AddSingleton(enricher);
                }
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

    private static async Task DispatchCaptureAsync(
        HubConnection connection, Dictionary<string, string>? headers)
    {
        var payload = JsonSerializer.SerializeToElement(new { });
        Dictionary<string, string>? noContext = null;
        var result = await connection.InvokeAsync<DispatchResult<JsonElement?>>(
            "Dispatch", CaptureType, payload, noContext, headers);
        result.Error.Should().BeNull();
    }

    [TestMethod]
    public async Task ContributeServerSetPropertyToDispatch()
    {
        // Arrange
        await using var host = CreateHost(new StaticValueEnricher("UserId", "emp-42"));
        var captured = host.GetRequiredService<CapturedProperties>();
        await using var connection = await ConnectToHub(host.Address);

        // Act
        await DispatchCaptureAsync(connection, headers: null);

        // Assert
        captured.Properties.Should().NotBeNull();
        captured.Properties!.Should().ContainKey("UserId");
        captured.Properties["UserId"].Should().Be("emp-42");
    }

    [TestMethod]
    public async Task OverrideClientSuppliedHeaderOfSameKey()
    {
        // Arrange — server enricher must win over a spoofed client header.
        await using var host = CreateHost(new StaticValueEnricher("UserId", "server-trusted"));
        var captured = host.GetRequiredService<CapturedProperties>();
        await using var connection = await ConnectToHub(host.Address);
        var spoofedHeaders = new Dictionary<string, string> { ["UserId"] = "client-spoofed" };

        // Act
        await DispatchCaptureAsync(connection, spoofedHeaders);

        // Assert
        captured.Properties.Should().NotBeNull();
        captured.Properties!["UserId"].Should().Be("server-trusted");
    }

    [TestMethod]
    public async Task LeaveHeadersUnchangedWhenNoEnrichersRegistered()
    {
        // Arrange — no enrichers: client headers still flow through unchanged.
        await using var host = CreateHost();
        var captured = host.GetRequiredService<CapturedProperties>();
        await using var connection = await ConnectToHub(host.Address);
        var headers = new Dictionary<string, string> { ["TenantCode"] = "acme" };

        // Act
        await DispatchCaptureAsync(connection, headers);

        // Assert
        captured.Properties.Should().NotBeNull();
        captured.Properties!["TenantCode"].Should().Be("acme");
    }

    [TestMethod]
    public async Task ComposeMultipleEnrichers()
    {
        // Arrange
        await using var host = CreateHost(
            new StaticValueEnricher("UserId", "emp-7"),
            new StaticValueEnricher("Tenant", "acme"));
        var captured = host.GetRequiredService<CapturedProperties>();
        await using var connection = await ConnectToHub(host.Address);

        // Act
        await DispatchCaptureAsync(connection, headers: null);

        // Assert
        captured.Properties.Should().NotBeNull();
        captured.Properties!["UserId"].Should().Be("emp-7");
        captured.Properties["Tenant"].Should().Be("acme");
    }
}
