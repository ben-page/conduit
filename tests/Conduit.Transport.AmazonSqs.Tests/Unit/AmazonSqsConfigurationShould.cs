using Amazon;
using Amazon.Runtime;
using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AmazonSqs.Tests.Unit;

[TestClass]
public class AmazonSqsConfigurationShould
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
    public async Task BindRegionCredentialsAndScalars()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:events:Region"] = "eu-west-1",
            ["Conduit:Endpoints:events:Credentials:AccessKey"] = "AKIAEXAMPLE",
            ["Conduit:Endpoints:events:Credentials:SecretKey"] = "secret",
            ["Conduit:Endpoints:events:VisibilityTimeoutSeconds"] = "120",
            ["Conduit:Endpoints:events:MaxReceiveCount"] = "3",
            ["Conduit:Endpoints:events:ServiceUri"] = "http://localhost:4566",
        });

        // Act
        builder.AddEndpoint("events", endpoint => endpoint.UseAmazonSqs());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>("events");
        options.Region.Should().Be(RegionEndpoint.EUWest1);
        options.Credentials.Should().BeOfType<BasicAWSCredentials>();
        options.VisibilityTimeoutSeconds.Should().Be(120);
        options.MaxReceiveCount.Should().Be(3);
        options.ServiceUri.Should().Be(new Uri("http://localhost:4566"));
    }

    [TestMethod]
    public async Task BuildSessionCredentialsWhenATokenIsSupplied()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:events:Region"] = "eu-west-1",
            ["Conduit:Endpoints:events:Credentials:AccessKey"] = "AKIAEXAMPLE",
            ["Conduit:Endpoints:events:Credentials:SecretKey"] = "secret",
            ["Conduit:Endpoints:events:Credentials:SessionToken"] = "token",
        });

        // Act
        builder.AddEndpoint("events", endpoint => endpoint.UseAmazonSqs());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>("events")
            .Credentials.Should().BeOfType<SessionAWSCredentials>();
    }

    [TestMethod]
    public async Task LetCodeOverrideTheConfiguredRegion()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:events:Region"] = "eu-west-1",
        });

        // Act
        builder.AddEndpoint("events", endpoint => endpoint.UseAmazonSqs(
            options => options.Region = RegionEndpoint.USEast1));

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>("events")
            .Region.Should().Be(RegionEndpoint.USEast1);
    }

    [TestMethod]
    public async Task LeaveCredentialsNullForTheDefaultAwsChain()
    {
        // Arrange
        var builder = BuilderWith(new Dictionary<string, string?>
        {
            ["Conduit:Endpoints:events:Region"] = "eu-west-1",
        });

        // Act
        builder.AddEndpoint("events", endpoint => endpoint.UseAmazonSqs());

        // Assert
        await using var serviceProvider = builder.Services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<AmazonSqsOptions>("events")
            .Credentials.Should().BeNull();
    }
}
