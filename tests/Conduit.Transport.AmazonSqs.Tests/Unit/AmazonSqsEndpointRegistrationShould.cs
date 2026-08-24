using Amazon;
using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AmazonSqs.Tests.Unit;

[TestClass]
public class AmazonSqsEndpointRegistrationShould
{
    private static ServiceProvider BuildProviderWithTwoEndpoints()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConduitOptions());

        var builder = new ConduitBuilder(services);
        builder.AddEndpoint("orders", endpoint => endpoint.UseAmazonSqs(
            options => options.Region = RegionEndpoint.EUWest1));
        builder.AddEndpoint("audit", endpoint => endpoint.UseAmazonSqs(
            options => options.Region = RegionEndpoint.USEast1));

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task GiveEachEndpointItsOwnOptions()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>("orders");
        var audit = serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>("audit");

        // Assert
        orders.Region.Should().Be(RegionEndpoint.EUWest1);
        audit.Region.Should().Be(RegionEndpoint.USEast1);
    }

    [TestMethod]
    public async Task ResolveADistinctTransportPerEndpoint()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<IConduitTransport>("orders");
        var audit = serviceProvider.GetRequiredKeyedService<IConduitTransport>("audit");

        // Assert
        orders.Should().NotBeSameAs(audit);
    }

    [TestMethod]
    public async Task RegisterOneBroadcastTargetAndOneLifecyclePerEndpoint()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var transports = serviceProvider.GetServices<IConduitTransport>().ToList();
        var hostedServices = serviceProvider.GetServices<IHostedService>()
            .OfType<AmazonSqsTransport>()
            .ToList();

        // Assert
        transports.Distinct().Should().HaveCount(2);
        hostedServices.Distinct().Should().HaveCount(2);
    }
}
