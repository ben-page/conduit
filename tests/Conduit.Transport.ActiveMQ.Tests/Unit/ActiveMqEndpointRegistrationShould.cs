using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.ActiveMQ.Tests.Unit;

[TestClass]
public class ActiveMqEndpointRegistrationShould
{
    private static ServiceProvider BuildProviderWithTwoEndpoints()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConduitOptions());

        var builder = new ConduitBuilder(services);
        builder.AddEndpoint("orders", endpoint => endpoint.UseActiveMQ("amqp://alpha:5672"));
        builder.AddEndpoint("audit", endpoint => endpoint.UseActiveMQ("amqp://beta:5672"));

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public async Task GiveEachEndpointItsOwnOptions()
    {
        // Arrange
        await using var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<ActiveMQOptions>("orders");
        var audit = serviceProvider.GetRequiredKeyedService<ActiveMQOptions>("audit");

        // Assert
        orders.ConnectionString.Should().Be("amqp://alpha:5672");
        audit.ConnectionString.Should().Be("amqp://beta:5672");
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
            .OfType<ActiveMQTransport>()
            .ToList();

        // Assert
        transports.Distinct().Should().HaveCount(2);
        hostedServices.Distinct().Should().HaveCount(2);
    }
}
