using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.Http.Tests.Unit;

[TestClass]
public class HttpConfigurationShould
{
    [TestMethod]
    public async Task ResolveATransportFromTheConfiguredBaseAddress()
    {
        // Arrange
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Conduit:Endpoints:billing:BaseAddress"] = "https://billing.example",
                ["Conduit:Endpoints:billing:BasePath"] = "/api/messages",
            })
            .Build();
        var builder = new ConduitBuilder(services);
        builder.UseConfiguration(configuration);

        // Act
        builder.AddEndpoint("billing", endpoint => endpoint.UseHttp());

        // Assert
        await using var serviceProvider = services.BuildServiceProvider();
        serviceProvider.GetRequiredKeyedService<IConduitTransport>("billing")
            .Should().BeOfType<HttpTransport>();

        var options = serviceProvider.GetRequiredKeyedService<HttpOptions>("billing");
        options.BaseAddress.Should().Be(new Uri("https://billing.example"));
        options.BasePath.Should().Be("/api/messages");
    }

    [TestMethod]
    public void ExplainWhichKeyIsMissingWhenNoBaseAddressIsSupplied()
    {
        // Arrange
        var builder = new ConduitBuilder(new ServiceCollection());

        // Act
        var act = () => builder.AddEndpoint("billing", endpoint => endpoint.UseHttp());

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Conduit:Endpoints:billing:BaseAddress*");
    }
}
