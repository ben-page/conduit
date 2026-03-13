using AwesomeAssertions;
using Conduit.Routing;

namespace Conduit.Tests;

[TestClass]
public class DefaultRouteResolverTests
{
    [TestMethod]
    public void ReturnsNull_WhenNoRoutesConfigured()
    {
        var options = new ConduitOptions();
        var resolver = new DefaultRouteResolver(options);

        var result = resolver.Resolve("Orders.Commands.CreateOrder");

        result.Should().BeNull();
    }

    [TestMethod]
    public void MatchesNamespacePrefix()
    {
        var options = new ConduitOptions();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };
        options.Endpoints["orders-api"] = endpoint;
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "orders-api" });

        var resolver = new DefaultRouteResolver(options);

        var result = resolver.Resolve("Orders.Commands.CreateOrder");

        result.Should().NotBeNull();
        result!.Name.Should().Be("orders-api");
    }

    [TestMethod]
    public void ReturnsNull_WhenNoPatternMatches()
    {
        var options = new ConduitOptions();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };
        options.Endpoints["orders-api"] = endpoint;
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "orders-api" });

        var resolver = new DefaultRouteResolver(options);

        var result = resolver.Resolve("Billing.Commands.ChargeCard");

        result.Should().BeNull();
    }

    [TestMethod]
    public void FirstMatchWins_WhenMultipleRoutesMatch()
    {
        var options = new ConduitOptions();
        options.Endpoints["specific"] = new ConduitEndpoint { Name = "specific" };
        options.Endpoints["general"] = new ConduitEndpoint { Name = "general" };
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands.Create", EndpointName = "specific" });
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "general" });

        var resolver = new DefaultRouteResolver(options);

        var result = resolver.Resolve("Orders.Commands.CreateOrder");

        result!.Name.Should().Be("specific");
    }

    [TestMethod]
    public void ReturnsNull_WhenEndpointNotRegistered()
    {
        var options = new ConduitOptions();
        options.Routes.Add(new ConduitRoute { Pattern = "Orders", EndpointName = "missing" });

        var resolver = new DefaultRouteResolver(options);

        var result = resolver.Resolve("Orders.Commands.CreateOrder");

        result.Should().BeNull();
    }
}

[TestClass]
public class ConduitBuilderTests
{
    [TestMethod]
    public void AddEndpoint_RegistersEndpointInOptions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        builder.AddEndpoint("test", endpoint => { });

        builder.Options.Endpoints.Should().ContainKey("test");
        builder.Options.Endpoints["test"].Name.Should().Be("test");
    }

    [TestMethod]
    public void Route_String_AddsRouteToOptions()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        builder.Route("Orders.Commands").To("orders-api");

        builder.Options.Routes.Should().HaveCount(1);
        builder.Options.Routes[0].Pattern.Should().Be("Orders.Commands");
        builder.Options.Routes[0].EndpointName.Should().Be("orders-api");
    }

    [TestMethod]
    public void Route_Generic_UsesTypeNamespace()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        var builder = new ConduitBuilder(services);

        builder.Route<ConduitBuilder>().To("test");

        builder.Options.Routes.Should().HaveCount(1);
        builder.Options.Routes[0].Pattern.Should().Be("Conduit.Routing");
    }
}
