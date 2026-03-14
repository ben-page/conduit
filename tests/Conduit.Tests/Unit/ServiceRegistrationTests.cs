using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class ServiceRegistrationTests
{
    [TestMethod]
    public void AddConduit_RegistersRouteResolver()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddConduit();
        var serviceProvider = services.BuildServiceProvider();

        // Act
        var resolver = serviceProvider.GetService<IRouteResolver>();

        // Assert
        resolver.Should().NotBeNull();
        resolver.Should().BeOfType<DefaultRouteResolver>();
    }

    [TestMethod]
    public void AddConduit_RegistersConduitOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddConduit();
        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetService<ConduitOptions>();

        // Assert
        options.Should().NotBeNull();
    }
}
