using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

[TestClass]
public class AsbEndpointRegistrationShould
{
    private const string AlphaConnectionString =
        "Endpoint=sb://alpha.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=c2VjcmV0";

    private const string BetaConnectionString =
        "Endpoint=sb://beta.servicebus.windows.net/;SharedAccessKeyName=key;SharedAccessKey=c2VjcmV0";

    private static ServiceProvider BuildProviderWithTwoEndpoints()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConduitOptions());

        var builder = new ConduitBuilder(services);
        builder.AddEndpoint("orders", endpoint => endpoint.UseAzureServiceBus(AlphaConnectionString));
        builder.AddEndpoint("audit", endpoint => endpoint.UseAzureServiceBus(BetaConnectionString));

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task GiveEachEndpointItsOwnOptions()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<AzureServiceBusOptions>("orders");
        var audit = serviceProvider.GetRequiredKeyedService<AzureServiceBusOptions>("audit");

        // Assert
        orders.ConnectionString.Should().Be(AlphaConnectionString);
        audit.ConnectionString.Should().Be(BetaConnectionString);
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
            .OfType<AzureServiceBusTransport>()
            .ToList();

        // Assert
        transports.Distinct().Should().HaveCount(2);
        hostedServices.Distinct().Should().HaveCount(2);
    }
}
