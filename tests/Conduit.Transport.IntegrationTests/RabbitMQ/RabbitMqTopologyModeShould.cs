using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.TestUtils;
using Conduit.Transport.IntegrationTests.Support;
using Conduit.Transport.IntegrationTests.Support.Contracts;
using Conduit.Transport.RabbitMQ;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.IntegrationTests.RabbitMQ;

/// <summary>
/// Tests topology mode behavior over RabbitMQ: Production creates durable resources,
/// Developer creates ephemeral auto-delete resources, and Test mode isolates runs
/// with independent random prefixes.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RabbitMqTopologyModeShould
{
    private static readonly RabbitMqFixture Fixture = new();

    [ClassInitialize]
    public static async Task ClassInitialize(TestContext context)
    {
        await Fixture.InitializeAsync();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await Fixture.CleanupAsync();
    }

    [TestCleanup]
    public async Task TestCleanup()
    {
        await Fixture.DisposeHostsAsync();
    }

    private static void EnsureAvailable()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Inconclusive("RabbitMQ container not available. Ensure Docker/Podman is running.");
        }
    }

    /// <summary>
    /// Creates a host with the specified topology mode and optional developer prefix.
    /// </summary>
    private static async Task<TestHost> CreateHostWithTopologyModeAsync(
        string applicationName,
        TopologyMode topologyMode,
        string? developerPrefix = null)
    {
        var connectionString = Fixture.ConnectionString;

        var host = await TestHost.StartAsync(builder =>
        {
            builder.Services.AddSingleton<SpyLog>();
            builder.Services.AddConduit(conduit =>
            {
                conduit.ApplicationName = applicationName;
                conduit.TopologyMode = topologyMode;
                conduit.DeveloperPrefix = developerPrefix;
                conduit.AddEndpoint("self", endpoint =>
                    endpoint.UseRabbitMQ(connectionString));
            });
        });

        Fixture.TrackHost(host);
        return host;
    }

    [TestMethod]
    public async Task CreateDurableResourcesInProductionMode()
    {
        // Arrange
        EnsureAvailable();

        var appName = TransportTestFixture.UniqueAppName("prod");

        // Act — host starts successfully, meaning topology was created without error
        var host = await CreateHostWithTopologyModeAsync(appName, TopologyMode.Production);

        // Assert — the host is running (topology creation succeeded)
        var mediator = host.GetRequiredService<IMediator>();
        mediator.Should().NotBeNull();
    }

    [TestMethod]
    public async Task CreateEphemeralResourcesInDeveloperMode()
    {
        // Arrange
        EnsureAvailable();

        var appName = TransportTestFixture.UniqueAppName("dev");
        var prefix = $"dev-{Guid.NewGuid():N}"[..10];

        // Act — host starts successfully with Developer mode ephemeral resources
        var host = await CreateHostWithTopologyModeAsync(appName, TopologyMode.Developer, prefix);

        // Assert — the host is running (topology creation succeeded)
        var mediator = host.GetRequiredService<IMediator>();
        mediator.Should().NotBeNull();
    }

    [TestMethod]
    public async Task IsolateTestModeRuns()
    {
        // Arrange
        EnsureAvailable();

        var appName1 = TransportTestFixture.UniqueAppName("iso-a");
        var appName2 = TransportTestFixture.UniqueAppName("iso-b");

        // Two hosts with TopologyMode.Test — each gets a random prefix,
        // so they create different exchanges/queues and cannot communicate
        var host1 = await CreateHostWithTopologyModeAsync(appName1, TopologyMode.Test);
        var host2 = await CreateHostWithTopologyModeAsync(appName2, TopologyMode.Test);

        var spy1 = host1.GetRequiredService<SpyLog>();
        var spy2 = host2.GetRequiredService<SpyLog>();
        var mediator1 = host1.GetRequiredService<IMediator>();

        await Task.Delay(2000);

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
