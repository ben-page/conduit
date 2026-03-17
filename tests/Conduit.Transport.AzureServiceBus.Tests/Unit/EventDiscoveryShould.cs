using AwesomeAssertions;
using Conduit.Handlers;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Conduit.Transport.AzureServiceBus.Tests.Unit;

[TestClass]
public class EventDiscoveryShould
{
    [TestMethod]
    public void DiscoverEventTypesFromServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddTransient<IEventListener<ItemCreatedEvent>, ItemCreatedListener>();
        services.AddSingleton<EventSpy<ItemCreatedEvent>>();

        var options = new AzureServiceBusOptions { ConnectionString = "not-used" };
        var logger = new LoggerFactory().CreateLogger<AzureServiceBusTransport>();
        var transport = new AzureServiceBusTransport(options, services, logger);

        // Act — use reflection to test the private method
        var method = typeof(AzureServiceBusTransport)
            .GetMethod("DiscoverSubscribedEventTypes",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var result = (List<string>)method!.Invoke(transport, null)!;

        // Assert
        result.Should().Contain(
            "Conduit.Transport.AzureServiceBus.Tests.Support.ItemCreatedEvent");
    }
}
