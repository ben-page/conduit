using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Dispatch;
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
        var endpoint = new ConduitEndpoint { Name = "orders-api", Transport = new StubTransport() };
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
        options.Endpoints["specific"] = new ConduitEndpoint { Name = "specific", Transport = new StubTransport() };
        options.Endpoints["general"] = new ConduitEndpoint { Name = "general", Transport = new StubTransport() };
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands.Create", EndpointName = "specific" });
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "general" });

        var resolver = new DefaultRouteResolver(options);

        var result = resolver.Resolve("Orders.Commands.CreateOrder");

        result!.Name.Should().Be("specific");
    }

    [TestMethod]
    public void ThrowsWhenEndpointNotRegistered()
    {
        var options = new ConduitOptions();
        options.Routes.Add(new ConduitRoute { Pattern = "Orders", EndpointName = "missing" });

        var resolver = new DefaultRouteResolver(options);

        var action = () => resolver.Resolve("Orders.Commands.CreateOrder");

        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*endpoint 'missing' is not registered*");
    }

    [TestMethod]
    public void ThrowsWhenEndpointHasNoTransport()
    {
        var options = new ConduitOptions();
        options.Endpoints["orders-api"] = new ConduitEndpoint { Name = "orders-api" };
        options.Routes.Add(new ConduitRoute { Pattern = "Orders", EndpointName = "orders-api" });

        var resolver = new DefaultRouteResolver(options);

        var action = () => resolver.Resolve("Orders.Commands.CreateOrder");

        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*no transport configured*");
    }
}

file class StubTransport : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;
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
        builder.Options.Routes[0].Pattern.Should().Be("Conduit.Configuration");
    }
}
