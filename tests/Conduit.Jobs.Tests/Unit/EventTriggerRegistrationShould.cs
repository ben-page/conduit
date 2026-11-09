using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Jobs.Tests.Support;
using Conduit.Persistence.Sqlite;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Jobs.Tests.Unit;

/// <summary>
/// A trigger's listener is registered at runtime, so the generator never saw it: <c>AddJobs</c> activates the trigger's
/// event in the type registry, or a broker transport (which subscribes the registry's active events) would never bind
/// the event that fires the trigger.
/// </summary>
[TestClass]
public class EventTriggerRegistrationShould
{
    [TestMethod]
    public void ActivateTheTriggeredEventInTheRegistry()
    {
        // Arrange -- nothing in this assembly listens for ThingHappened or CartAbandoned at compile time
        var services = new ServiceCollection();

        // Act
        services.AddConduit(conduit =>
        {
            conduit.UseSqlite("Data Source=:memory:");
            conduit.AddJobs(jobs => jobs.On<ThingHappened>("notify").Run(thing => new RecordedJob(thing.Label)));
        });

        // Assert -- the triggered event is subscribed; an event no trigger names is known but not subscribed
        var registry = (ConduitTypeRegistry)services.Single(descriptor => descriptor.ServiceType == typeof(ConduitTypeRegistry))
            .ImplementationInstance!;
        registry.ActiveEventTypeNames.Should().Contain(typeof(ThingHappened).FullName!)
            .And.NotContain(typeof(CartAbandoned).FullName!);
        registry.EventTypeNames.Should().Contain(typeof(CartAbandoned).FullName!);
    }
}
