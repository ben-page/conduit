using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.RabbitMQ.Tests.Unit;

[TestClass]
public class RabbitMqConfigurationShould
{
    private static ConduitBuilder BuilderWith(Dictionary<string, string?> values)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new ConduitOptions());

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var builder = new ConduitBuilder(services);
        builder.UseConfiguration(configuration);

        return builder;
    }

    [TestMethod]
    public async Task BindOptionsFromTheEndpointSection()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:orders:ConnectionString"] = "amqp://guest@alpha:5672/",
            ["Conduit:Endpoints:orders:DefaultConcurrency"] = "20",
            ["Conduit:Endpoints:orders:ResponseTimeout"] = "00:00:30",
        });

        // Act
        builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<RabbitMQOptions>("orders");
        options.ConnectionString.Should().Be("amqp://guest@alpha:5672/");
        options.DefaultConcurrency.Should().Be(20);
        options.ResponseTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public async Task LetTheExplicitArgumentOverrideConfiguration()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:orders:ConnectionString"] = "amqp://guest@from-config:5672/",
        });

        // Act
        builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ("amqp://guest@from-code:5672/"));

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<RabbitMQOptions>("orders")
            .ConnectionString.Should().Be("amqp://guest@from-code:5672/");
    }

    [TestMethod]
    public async Task MergeAConfiguredPoolWithCodeDeclaredMessageTypes()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:orders:ConnectionString"] = "amqp://guest@alpha:5672/",
            ["Conduit:Endpoints:orders:Pools:0:Name"] = "bulk",
            ["Conduit:Endpoints:orders:Pools:0:Concurrency"] = "50",
        });

        // Act
        builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ(
            configure: options => options.AddPool("bulk", pool => pool.Handle("Orders.Commands.Import"))));

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<RabbitMQOptions>("orders");
        options.Pools.Should().ContainSingle();
        options.Pools[0].Concurrency.Should().Be(50);
        options.Pools[0].MessageTypes.Should().Equal("Orders.Commands.Import");
    }

    [TestMethod]
    public async Task ExplainWhichKeyIsMissingWhenNoConnectionStringIsSupplied()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>());

        // Act
        var act = () => builder.AddEndpoint("orders", endpoint => endpoint.UseRabbitMQ());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Conduit:Endpoints:orders:ConnectionString*");
    }
}
