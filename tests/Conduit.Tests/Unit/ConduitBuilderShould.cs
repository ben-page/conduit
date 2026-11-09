using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Tests.Support;

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

    [TestMethod]
    public void RejectAClosedPipelineType()
    {
        // Arrange -- the runtime closes each pipeline type over the dispatch's own request and result types
        var builder = new ConduitBuilder(new Microsoft.Extensions.DependencyInjection.ServiceCollection());

        // Act
        var register = () => builder.AddPipeline(typeof(LoggingPipeline<SampleCommand, Conduit.Messages.Unit>));

        // Assert
        register.Should().ThrowExactly<ArgumentException>()
            .WithParameterName("behaviorType")
            .WithMessage("*open generic type with two type parameters*");
        builder.Options.PipelineTypes.Should().BeEmpty();
    }

    [TestMethod]
    public void RejectAWrongArityPipelineType()
    {
        // Arrange
        var builder = new ConduitBuilder(new Microsoft.Extensions.DependencyInjection.ServiceCollection());

        // Act
        var register = () => builder.AddPipeline(typeof(RequestOnlyPipeline<>));

        // Assert
        register.Should().ThrowExactly<ArgumentException>()
            .WithParameterName("behaviorType")
            .WithMessage("*open generic type with two type parameters*");
        builder.Options.PipelineTypes.Should().BeEmpty();
    }

    [TestMethod]
    public void RejectANullEndpointName()
    {
        // Arrange
        var builder = new ConduitBuilder(new Microsoft.Extensions.DependencyInjection.ServiceCollection());

        // Act
        var add = () => builder.AddEndpoint(null!, _ => { });

        // Assert
        add.Should().ThrowExactly<ArgumentNullException>().WithParameterName("name");
    }

    [TestMethod]
    public void RejectABlankEndpointName()
    {
        // Arrange
        var builder = new ConduitBuilder(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
        var configured = false;

        // Act
        var add = () => builder.AddEndpoint("  ", _ => configured = true);

        // Assert
        add.Should().ThrowExactly<ArgumentException>().WithParameterName("name");
        configured.Should().BeFalse();
        builder.Options.Endpoints.Should().BeEmpty();
    }

    [TestMethod]
    public void RejectADuplicateEndpointName()
    {
        // Arrange -- a second endpoint of the same name would replace the first and register a second transport
        var builder = new ConduitBuilder(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
        var first = new SpyTransport();
        builder.AddEndpoint("orders", endpoint => endpoint.UseTransport(first));
        var reconfigured = false;

        // Act
        var add = () => builder.AddEndpoint("orders", _ => reconfigured = true);

        // Assert
        add.Should().ThrowExactly<ArgumentException>()
            .WithParameterName("name")
            .WithMessage("*'orders' is already registered*");
        reconfigured.Should().BeFalse();
        builder.Options.Endpoints.Should().ContainSingle();
    }
}
