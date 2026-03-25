using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Tests event routing over Azure Service Bus: multicast delivery to all subscribing apps,
/// LocalOnly attribute suppression, and continue-on-failure semantics.
/// </summary>
/// <remarks>
/// Events use multicast subscriptions — each app with a different ApplicationName
/// gets its own subscription, so all subscribers receive a copy.
/// </remarks>
[TestClass]
public class AsbEventRoutingShould : AsbTestBase
{
    [TestMethod]
    public async Task DeliverEventToAllSubscribingApps()
    {
        // Arrange
        var appNameA = UniqueAppName("app-a");
        var appNameB = UniqueAppName("app-b");
        var publisherName = UniqueAppName("pub");

        await using var hostA = await CreateHostAsync(appNameA, configureHandling: handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        var prefix = hostA.TopologyPrefix!;
        await using var hostB = await CreateHostAsync(appNameB, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        await using var publisherHost = await CreateHostAsync(publisherName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });

        var spyA = hostA.GetRequiredService<SpyLog>();
        var spyB = hostB.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act
        await mediator.PublishAsync(new TestEvent("broadcast"));

        // Assert — both subscribing apps received the event
        await spyA.WaitForCountAsync(1);
        await spyB.WaitForCountAsync(1);
        spyA.Invocations.OfType<TestEvent>().Should().Contain(e => e.Value == "broadcast");
        spyB.Invocations.OfType<TestEvent>().Should().Contain(e => e.Value == "broadcast");
    }

    [TestMethod]
    public async Task NotBroadcastLocalOnlyEvent()
    {
        // Arrange
        var handlerName = UniqueAppName("handler");
        var publisherName = UniqueAppName("pub");

        await using var handlerHost = await CreateHostAsync(handlerName, configureHandling: handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });
        var prefix = handlerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(publisherName, prefix, handling =>
        {
            handling.ClearCommandHandlers();
            handling.ClearQueryHandlers();
        });

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var publisherSpy = publisherHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act — publish a [LocalOnly] event from the publisher
        await mediator.PublishAsync(new LocalOnlyTestEvent("local-only"));

        // Assert — publisher's local listener fires, but handler host does NOT receive it
        await publisherSpy.WaitForCountAsync(1);
        publisherSpy.Invocations.OfType<LocalOnlyTestEvent>().Should().Contain(
            e => e.Value == "local-only");

        // Give some time for any erroneous broker delivery
        await Task.Delay(3000);
        handlerSpy.Count.Should().Be(0);
    }

    [TestMethod]
    public async Task DeliverEventToListenerDespiteOtherListenerFailure()
    {
        // Arrange
        var appName = UniqueAppName("app");

        await using var host = await CreateHostAsync(appName);

        var spy = host.GetRequiredService<SpyLog>();
        var mediator = host.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act — ThrowingEventListener records then throws
        await mediator.PublishAsync(new ThrowingTestEvent("boom"));

        // Assert — the listener recorded the event before throwing
        await spy.WaitForCountAsync(1);
        spy.Invocations.OfType<ThrowingTestEvent>().Should().Contain(e => e.Value == "boom");
    }
}
