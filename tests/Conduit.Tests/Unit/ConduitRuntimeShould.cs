using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Dispatch;

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
        var endpoint = new ConduitEndpoint { Name = "orders-api", Transport = new StubTransport() };
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
        options.Endpoints["specific"] = new ConduitEndpoint { Name = "specific", Transport = new StubTransport() };
        options.Endpoints["general"] = new ConduitEndpoint { Name = "general", Transport = new StubTransport() };
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
        var options = new ConduitOptions();
        options.Endpoints["orders-api"] = new ConduitEndpoint { Name = "orders-api" };
        options.Routes.Add(new ConduitRoute { Pattern = "Orders", EndpointName = "orders-api" });

        var action = () => ConduitRuntime.ResolveRoute(options, "Orders.Commands.CreateOrder");

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
