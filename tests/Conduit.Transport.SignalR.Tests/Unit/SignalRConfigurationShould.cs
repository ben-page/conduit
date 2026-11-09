using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conduit.Transport.SignalR.Tests.Unit;

[TestClass]
public class SignalRConfigurationShould
{
    [TestMethod]
    public async Task ResolveATransportFromTheConfiguredHubUri()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Conduit:Endpoints:inventory:HubUri"] = "https://inventory.example/conduit",
            })
            .Build();
        var builder = new ConduitBuilder(services);
        builder.UseConfiguration(configuration);

        // Act
        builder.AddEndpoint("inventory", endpoint => endpoint.UseSignalR());

        // Assert
        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<IConduitTransport>("inventory")
            .Should().BeOfType<SignalRTransport>();

        var options = serviceProvider.GetRequiredKeyedService<SignalROptions>("inventory");
        options.HubUri.Should().Be(new Uri("https://inventory.example/conduit"));
    }

    [TestMethod]
    public void ExplainWhichKeyIsMissingWhenNoHubUriIsSupplied()
    {
        // Arrange
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        var act = () => builder.AddEndpoint("inventory", endpoint => endpoint.UseSignalR());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Conduit:Endpoints:inventory:HubUri*");
    }

    [TestMethod]
    public async Task LeaveTheHubDistrustingCallerContextUnlessARegistrationConfiguresIt()
    {
        // Arrange
        var defaulted = new ServiceCollection().AddConduitSignalR();
        var configured = new ServiceCollection().AddConduitSignalR(options => options.TrustCallerContext = true);
        var withServerInfo = new ServiceCollection()
            .AddConduitSignalR(new ConduitServerInfo("build-1"), options => options.TrustCallerContext = true);

        // Act
        await using var defaultedProvider = defaulted.BuildServiceProvider();
        await using var configuredProvider = configured.BuildServiceProvider();
        await using var withServerInfoProvider = withServerInfo.BuildServiceProvider();

        // Assert
        defaultedProvider.GetRequiredService<IOptions<ConduitSignalROptions>>().Value.TrustCallerContext.Should().BeFalse();
        configuredProvider.GetRequiredService<IOptions<ConduitSignalROptions>>().Value.TrustCallerContext.Should().BeTrue();
        withServerInfoProvider.GetRequiredService<IOptions<ConduitSignalROptions>>().Value.TrustCallerContext.Should().BeTrue();
        withServerInfoProvider.GetRequiredService<ConduitServerInfo>().Should().Be(new ConduitServerInfo("build-1"));
    }
}
