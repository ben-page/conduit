using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.RabbitMQ.Tests.Unit;

[TestClass]
public class RabbitMqEndpointRegistrationShould
{
    private static ServiceProvider BuildProviderWithTwoEndpoints()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConduitOptions());

        var builder = new ConduitBuilder(services);
        builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ("amqp://guest:guest@alpha:5672/"));
        builder.AddEndpoint("audit", endpoint => endpoint.UseRabbitMQ("amqp://guest:guest@beta:5672/"));

        return services.BuildServiceProvider();
    }

    [TestMethod]
    public void GiveEachEndpointItsOwnOptions()
    {
        // Arrange
        var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<RabbitMQOptions>("orders");
        var audit = serviceProvider.GetRequiredKeyedService<RabbitMQOptions>("audit");

        // Assert
        orders.ConnectionString.Should().Be("amqp://guest:guest@alpha:5672/");
        audit.ConnectionString.Should().Be("amqp://guest:guest@beta:5672/");
        orders.EndpointName.Should().Be("orders");
        audit.EndpointName.Should().Be("audit");
    }

    [TestMethod]
    public void ResolveADistinctTransportPerEndpoint()
    {
        // Arrange
        var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var orders = serviceProvider.GetRequiredKeyedService<IConduitTransport>("orders");
        var audit = serviceProvider.GetRequiredKeyedService<IConduitTransport>("audit");

        // Assert
        orders.Should().NotBeSameAs(audit);
    }

    [TestMethod]
    public void BroadcastToOneTransportInstancePerEndpoint()
    {
        // Arrange
        var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var transports = serviceProvider.GetServices<IConduitTransport>().ToList();

        // Assert
        transports.Should().HaveCount(2);
        transports.Distinct().Should().HaveCount(2);
    }

    [TestMethod]
    public void StartOneHostedServicePerEndpoint()
    {
        // Arrange
        var serviceProvider = BuildProviderWithTwoEndpoints();

        // Act
        var hostedServices = serviceProvider.GetServices<IHostedService>()
            .OfType<RabbitMQTransport>()
            .ToList();

        // Assert
        hostedServices.Should().HaveCount(2);
        hostedServices.Distinct().Should().HaveCount(2);
    }
}
