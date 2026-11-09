using AwesomeAssertions;
using Conduit.Audit.Runtime;
using Conduit.Configuration;
using Conduit.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Audit.Tests.Unit;

/// <summary>
/// <c>AddAuditListener</c> registers <see cref="LocalAuditListener"/> at runtime (it is <c>[NotAutoRegistered]</c>), so
/// the generator never saw it: the listener activates <see cref="AuditBatch"/> in the type registry, or a central
/// collector on a broker transport (which subscribes the registry's active events) would never receive a batch.
/// </summary>
[TestClass]
public sealed class AuditListenerRegistrationShould
{
    [TestMethod]
    public void ActivateTheAuditBatchInTheRegistry()
    {
        // Arrange -- a registry that knows the event but found no listener for it, as a collector host's does
        var registry = new ConduitTypeRegistry(
            eventTypes: [typeof(AuditBatch)],
            commandTypes: [],
            queryTypes: [],
            listenedEventTypes: []);
        var services = new ServiceCollection();
        services.AddSingleton(registry);

        // Act
        new ConduitBuilder(services).AddAuditListener();

        // Assert
        registry.ActiveEventTypeNames.Should().Equal(typeof(AuditBatch).FullName);
    }
}
