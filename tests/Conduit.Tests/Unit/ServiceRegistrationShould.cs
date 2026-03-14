using AwesomeAssertions;
using Conduit.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class ServiceRegistrationShould
{
    [TestMethod]
    public void RegisterConduitOptions()
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
