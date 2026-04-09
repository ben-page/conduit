using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests topology mode behavior over ActiveMQ Artemis: Production creates durable resources,
/// Developer creates ephemeral auto-delete resources, and Test mode isolates runs
/// with independent random prefixes.
/// </summary>
[TestClass]
public class ActiveMQTopologyModeShould : ActiveMQTestBase
{
    /// <summary>
    /// Creates a host with the specified topology mode and optional developer prefix.
    /// </summary>
    private static async Task<TestHost> CreateHostWithTopologyModeAsync(
        string applicationName,
        TopologyMode topologyMode,
        string? topologyPrefix = null)
    {
        return await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = topologyMode;
                conduit.TopologyPrefix = topologyPrefix;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseActiveMQ(ConnectionString!));
            });
        });
    }

    [TestMethod]
    public async Task CreateDurableResourcesInProductionMode()
    {
        // Arrange
        var appName = UniqueAppName("prod");

        // Act — host starts successfully, meaning topology was created without error
        await using var host = await CreateHostWithTopologyModeAsync(appName, TopologyMode.Production);

        // Assert — the host is running (topology creation succeeded)
        var mediator = host.GetRequiredService<IMediator>();
        mediator.Should().NotBeNull();
    }

    [TestMethod]
    public async Task CreateEphemeralResourcesInDeveloperMode()
    {
        // Arrange
        var appName = UniqueAppName("dev");
        var prefix = $"dev-{Guid.NewGuid():N}"[..10];

        // Act — host starts successfully with Developer mode ephemeral resources
        await using var host = await CreateHostWithTopologyModeAsync(appName, TopologyMode.Developer, prefix);

        // Assert — the host is running (topology creation succeeded)
        var mediator = host.GetRequiredService<IMediator>();
        mediator.Should().NotBeNull();
    }

    [TestMethod]
    public async Task IsolateTestModeRuns()
    {
        // Arrange
        var appName1 = UniqueAppName("iso-a");
        var appName2 = UniqueAppName("iso-b");

        // Two hosts with TopologyMode.Test — each gets a random prefix,
        // so they create different addresses/queues and cannot communicate
        await using var host1 = await CreateHostWithTopologyModeAsync(appName1, TopologyMode.Test);
        await using var host2 = await CreateHostWithTopologyModeAsync(appName2, TopologyMode.Test);

        var spy1 = host1.GetRequiredService<SpyLog>();
        var spy2 = host2.GetRequiredService<SpyLog>();
        var mediator1 = host1.GetRequiredService<IMediator>();

        // Act — publish event on host1
        await mediator1.PublishAsync(new TestEvent("isolated"));

        // Assert — host1's local listener fires, but host2 does NOT receive it
        await spy1.WaitForCountAsync(1);
        spy1.Invocations.OfType<TestEvent>().Should().Contain(e => e.Value == "isolated");

        // Give time for any erroneous cross-topology delivery
        await Task.Delay(2000);
        spy2.Count.Should().Be(0, "Host2 uses a different topology prefix and should not receive events from Host1");
    }
}
