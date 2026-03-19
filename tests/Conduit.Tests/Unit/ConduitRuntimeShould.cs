using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitRuntimeShould
{
    [TestMethod]
    public void ReturnNullWhenNoRoutesConfigured()
    {
        var options = new ConduitOptions();

        var result = ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        result.Should().BeNull();
    }

    [TestMethod]
    public void MatchNamespacePrefix()
    {
        var options = new ConduitOptions();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };
        options.Endpoints["orders-api"] = endpoint;
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "orders-api" });

        var result = ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        result.Should().NotBeNull();
        result.Name.Should().Be("orders-api");
    }

    [TestMethod]
    public void ReturnNullWhenNoPatternMatches()
    {
        var options = new ConduitOptions();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };
        options.Endpoints["orders-api"] = endpoint;
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "orders-api" });

        var result = ConduitRuntime.ResolveRoute(options, "Billing.Commands.ChargeCard");

        result.Should().BeNull();
    }

    [TestMethod]
    public void PickFirstMatchWhenMultipleRoutesMatch()
    {
        var options = new ConduitOptions();
        options.Endpoints["specific"] = new ConduitEndpoint { Name = "specific" };
        options.Endpoints["general"] = new ConduitEndpoint { Name = "general" };
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands.Create", EndpointName = "specific" });
        options.Routes.Add(new ConduitRoute { Pattern = "Orders.Commands", EndpointName = "general" });

        var result = ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        result!.Name.Should().Be("specific");
    }

    [TestMethod]
    public void ThrowWhenEndpointNotRegistered()
    {
        var options = new ConduitOptions();
        options.Routes.Add(new ConduitRoute { Pattern = "Orders", EndpointName = "missing" });

        var action = () => ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*endpoint 'missing' is not registered*");
    }

    [TestMethod]
    public void ThrowWhenEndpointHasNoTransport()
    {
        // Arrange — endpoint exists but no keyed IConduitTransport registered
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };

        // Act
        var action = () => ConduitRuntime.ResolveTransport(serviceProvider, endpoint);

        // Assert
        action.Should().ThrowExactly<InvalidOperationException>()
            .WithMessage("*no transport configured*");
    }

    [TestMethod]
    public void ResolveTransportFromKeyedService()
    {
        // Arrange
        var stubTransport = new StubTransport();
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IConduitTransport>("orders-api", stubTransport);
        var serviceProvider = services.BuildServiceProvider();
        var endpoint = new ConduitEndpoint { Name = "orders-api" };

        // Act
        var result = ConduitRuntime.ResolveTransport(serviceProvider, endpoint);

        // Assert
        result.Should().BeSameAs(stubTransport);
    }
}

file class StubTransport : IConduitTransport
{
    public Task<object?> SendRequestAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.FromResult<object?>(null);

    public Task SendEventAsync(DispatchEnvelope envelope, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
