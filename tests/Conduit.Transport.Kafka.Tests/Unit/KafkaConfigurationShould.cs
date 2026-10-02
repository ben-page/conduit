using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.Kafka.Tests.Unit;

[TestClass]
public class KafkaConfigurationShould
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
    public async Task BindProducerAndConsumerConfigByPropertyName()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:telemetry:BootstrapServers"] = "kafka:9092",
            ["Conduit:Endpoints:telemetry:ProducerConfig:LingerMs"] = "5",
            ["Conduit:Endpoints:telemetry:ProducerConfig:Acks"] = "All",
            ["Conduit:Endpoints:telemetry:ConsumerConfig:SessionTimeoutMs"] = "45000",
        });

        // Act
        builder.AddEndpoint("telemetry", endpoint => endpoint.UseKafka());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<KafkaOptions>("telemetry");
        options.BootstrapServers.Should().Be("kafka:9092");
        options.ProducerConfig!.LingerMs.Should().Be(5);
        options.ConsumerConfig!.SessionTimeoutMs.Should().Be(45000);
    }

    [TestMethod]
    public async Task AcceptDottedLibrdkafkaKeys()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:telemetry:BootstrapServers"] = "kafka:9092",
            ["Conduit:Endpoints:telemetry:ProducerConfig:linger.ms"] = "7",
        });

        // Act
        builder.AddEndpoint("telemetry", endpoint => endpoint.UseKafka());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<KafkaOptions>("telemetry")
            .ProducerConfig!.LingerMs.Should().Be(7);
    }

    [TestMethod]
    public async Task LeaveProducerAndConsumerConfigNullWhenTheEndpointHasNoKafkaSpecificConfiguration()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:telemetry:BootstrapServers"] = "kafka:9092",
        });

        // Act
        builder.AddEndpoint("telemetry", endpoint => endpoint.UseKafka());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<KafkaOptions>("telemetry");
        options.ProducerConfig.Should().BeNull();
        options.ConsumerConfig.Should().BeNull();
    }

    [TestMethod]
    public void ExplainWhichKeyIsMissingWhenNoBootstrapServersAreSupplied()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>());

        // Act
        var act = () => builder.AddEndpoint("telemetry", endpoint => endpoint.UseKafka());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Conduit:Endpoints:telemetry:BootstrapServers*");
    }
}
