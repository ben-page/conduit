using AwesomeAssertions;
using Conduit.Configuration;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitBuilderShould
{
    [TestMethod]
    public void RegisterEndpointInOptions()
    {
        // Arrange
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        // Act
        builder.AddEndpoint("test", endpoint => { });

        // Assert
        builder.Options.Endpoints.Should().ContainKey("test");
        builder.Options.Endpoints["test"].Name.Should().Be("test");
    }

    [TestMethod]
    public void AddNamespaceRouteViaEndpointBuilder()
    {
        // Arrange
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        // Act
        builder.AddEndpoint("orders-api", endpoint =>
            endpoint.RouteNamespace<ConduitBuilder>());

        // Assert
        builder.Options.Routes.Should().HaveCount(1);
        builder.Options.Routes[0].Pattern.Should().Be("Conduit.Configuration");
        builder.Options.Routes[0].EndpointName.Should().Be("orders-api");
        builder.Options.Routes[0].Exact.Should().BeFalse();
    }

    [TestMethod]
    public void AddTypeRouteViaEndpointBuilder()
    {
        // Arrange
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        // Act
        builder.AddEndpoint("orders-api", endpoint =>
            endpoint.RouteType<ConduitBuilder>());

        // Assert
        builder.Options.Routes.Should().HaveCount(1);
        builder.Options.Routes[0].Pattern.Should().Be("Conduit.Configuration.ConduitBuilder");
        builder.Options.Routes[0].EndpointName.Should().Be("orders-api");
        builder.Options.Routes[0].Exact.Should().BeTrue();
    }
}
