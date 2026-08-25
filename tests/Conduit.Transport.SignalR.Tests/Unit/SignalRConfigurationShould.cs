using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    }

    [TestMethod]
    public async Task ExplainWhichKeyIsMissingWhenNoHubUriIsSupplied()
    {
        // Arrange
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        var act = () => builder.AddEndpoint("inventory", endpoint => endpoint.UseSignalR());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Conduit:Endpoints:inventory:HubUri*");
    }
}
