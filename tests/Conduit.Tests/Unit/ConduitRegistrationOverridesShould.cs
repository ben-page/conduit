using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Unit;

[TestClass]
public class ConduitRegistrationOverridesShould
{
    [TestMethod]
    public void ActivateAnEventHandledAtRuntime()
    {
        // Arrange -- the generator found a listener for ItemCreatedEvent only
        var registry = new ConduitTypeRegistry(
            eventTypes: [typeof(ItemCreatedEvent), typeof(SimpleEvent), typeof(NestedEventOne)],
            commandTypes: [],
            queryTypes: [],
            listenedEventTypes: [typeof(ItemCreatedEvent)]);
        var services = new ServiceCollection();
        services.AddSingleton(registry);

        // Act -- the host's listeners are replaced by one for an event nothing listened for at compile time
        services.ConfigureConduitRegistrations(overrides => overrides
            .ClearEventListeners()
            .HandleEvent<SimpleEvent, SimpleEventListener>());

        // Assert -- only the handled event is subscribed
        registry.ActiveEventTypeNames.Should().Equal(typeof(SimpleEvent).FullName);
    }
}
