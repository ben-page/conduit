using AwesomeAssertions;
using Conduit.Configuration;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitBuilderShould
{
    [TestMethod]
    public void RegisterEndpointInOptions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        builder.AddEndpoint("test", endpoint => { });

        builder.Options.Endpoints.Should().ContainKey("test");
        builder.Options.Endpoints["test"].Name.Should().Be("test");
    }

    [TestMethod]
    public void AddStringRouteToOptions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        builder.Route("Orders.Commands").To("orders-api");

        builder.Options.Routes.Should().HaveCount(1);
        builder.Options.Routes[0].Pattern.Should().Be("Orders.Commands");
        builder.Options.Routes[0].EndpointName.Should().Be("orders-api");
    }

    [TestMethod]
    public void AddGenericRouteUsingTypeNamespace()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        builder.Route<ConduitBuilder>().To("test");

        builder.Options.Routes.Should().HaveCount(1);
        builder.Options.Routes[0].Pattern.Should().Be("Conduit.Configuration");
    }
}
