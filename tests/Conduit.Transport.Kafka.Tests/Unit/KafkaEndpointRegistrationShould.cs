using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Kafka.Tests.Unit;

[TestClass]
public class KafkaEndpointRegistrationShould
{
    private static ServiceProvider BuildProviderWithTwoEndpoints()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConduitOptions());

        var builder = new ConduitBuilder(services);
        builder.AddEndpoint("orders", endpoint => endpoint.UseKafka("alpha:9092"));
        builder.AddEndpoint("telemetry", endpoint => endpoint.UseKafka("beta:9092"));

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task GiveEachEndpointItsOwnOptions()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<KafkaOptions>("orders");
        var telemetry = serviceProvider.GetRequiredKeyedService<KafkaOptions>("telemetry");

        // Assert
        orders.BootstrapServers.Should().Be("alpha:9092");
        telemetry.BootstrapServers.Should().Be("beta:9092");
    }

    [TestMethod]
    public async Task ResolveADistinctTransportPerEndpoint()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<IConduitTransport>("orders");
        var telemetry = serviceProvider.GetRequiredKeyedService<IConduitTransport>("telemetry");

        // Assert
        orders.Should().NotBeSameAs(telemetry);
    }

    [TestMethod]
    public async Task RegisterOneBroadcastTargetAndOneLifecyclePerEndpoint()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var transports = serviceProvider.GetServices<IConduitTransport>().ToList();
        var hostedServices = serviceProvider.GetServices<IHostedService>()
            .OfType<KafkaTransport>()
            .ToList();

        // Assert
        transports.Distinct().Should().HaveCount(2);
        hostedServices.Distinct().Should().HaveCount(2);
    }
}
