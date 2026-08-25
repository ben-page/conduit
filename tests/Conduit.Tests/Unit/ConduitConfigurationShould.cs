using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitConfigurationShould
{
    private static IConfiguration BuildConfiguration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [TestMethod]
    public void BindCoreScalarsFromTheConduitSection()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Conduit:SystemName"] = "acme",
            ["Conduit:ApplicationName"] = "orders-api",
            ["Conduit:TopologyMode"] = "Developer",
            ["Conduit:TopologyPrefix"] = "ben",
            ["Conduit:ProducerOnly"] = "true",
        });
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        builder.UseConfiguration(configuration);

        // Assert
        builder.Options.SystemName.Should().Be("acme");
        builder.Options.ApplicationName.Should().Be("orders-api");
        builder.Options.TopologyMode.Should().Be(TopologyMode.Developer);
        builder.Options.TopologyPrefix.Should().Be("ben");
        builder.Options.ProducerOnly.Should().BeTrue();
    }

    [TestMethod]
    public void LeaveDefaultsUntouchedWhenKeysAreAbsent()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Conduit:SystemName"] = "acme",
        });
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        builder.UseConfiguration(configuration);

        // Assert
        builder.Options.SystemName.Should().Be("acme");
        builder.Options.TopologyMode.Should().Be(TopologyMode.Production);
        builder.Options.ProducerOnly.Should().BeFalse();
    }

    [TestMethod]
    public void NotBindEndpointSectionsOntoTheEndpointsDictionary()
    {
        // Arrange — Conduit:Endpoints holds transport options, not ConduitEndpoint instances
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:orders:ConnectionString"] = "amqp://guest@alpha:5672/",
        });
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        builder.UseConfiguration(configuration);

        // Assert
        builder.Options.Endpoints.Should().BeEmpty();
    }

    [TestMethod]
    public void AcceptASectionDirectlyWhenOneIsPassed()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Messaging:SystemName"] = "custom",
        });
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        builder.UseConfiguration(configuration.GetSection("Messaging"));

        // Assert
        builder.Options.SystemName.Should().Be("custom");
    }

    [TestMethod]
    public void LetCodeOverrideConfiguredValues()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Conduit:SystemName"] = "from-config",
        });
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        builder.UseConfiguration(configuration);
        builder.SystemName = "from-code";

        // Assert
        builder.Options.SystemName.Should().Be("from-code");
    }

    [TestMethod]
    public void BindCoreOptionsThroughTheGeneratedOverload()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Conduit:SystemName"] = "acme",
            ["Conduit:TopologyMode"] = "Developer",
        });
        var services = new ServiceCollection();

        // Act
        services.AddConduit(configuration, conduit => conduit.ApplicationName = "orders-api");

        // Assert
        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<ConduitOptions>();
        options.SystemName.Should().Be("acme");
        options.TopologyMode.Should().Be(TopologyMode.Developer);
        options.ApplicationName.Should().Be("orders-api");
    }

    [TestMethod]
    public void LetTheCallbackOverrideConfigurationInTheGeneratedOverload()
    {
        // Arrange
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Conduit:SystemName"] = "from-config",
        });
        var services = new ServiceCollection();

        // Act
        services.AddConduit(configuration, conduit => conduit.SystemName = "from-code");

        // Assert
        using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredService<ConduitOptions>().SystemName.Should().Be("from-code");
    }
}
