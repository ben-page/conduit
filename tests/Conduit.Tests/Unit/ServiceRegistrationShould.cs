using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Dispatch;
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
        ConduitRuntime.AddConduitCore(services);
        var serviceProvider = services.BuildServiceProvider();

        // Act
        var options = serviceProvider.GetService<ConduitOptions>();

        // Assert
        options.Should().NotBeNull();
    }
}
