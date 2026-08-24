using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// Guards the end-to-end shape of the self-published event fix: an application that publishes an
/// event and also listens to it runs its listeners exactly once, even though the broker routes the
/// application's own broadcast copy straight back through its per-app event queue.
/// </summary>
[TestClass]
public class SelfPublishedEventDeliveryShould
{
    [TestMethod]
    public async Task RunLocalListenersOnceWhenTheBrokerReturnsTheAppsOwnCopy()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });
        var options = host.GetRequiredService<ConduitOptions>();
        options.ApplicationName = "OrderService";
        var transport = new FakeBrokerTransport(options, host.Services);
        transport.Start();
        var mediator = host.GetRequiredService<IMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act — publish, then hand the app its own broadcast copy back, as the broker does
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "SelfPublished"));
        await spy.WaitForCountAsync(1);

        await transport.SendEventAsync(
            TestEnvelope.For(new ItemCreatedEvent(Guid.NewGuid(), "SelfPublished")),
            CancellationToken.None);
        var ownCopy = transport.PublishedEvents.Single();
        await transport.DeliverAsync(ownCopy);

        // Assert — the inline run is the only run
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task BindTheAppEventQueueToEventTypesThisAppPublishes()
    {
        // Arrange
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddConduit();
        });

        // Act — the list every broker transport binds its per-app event queue to
        var registry = host.GetRequiredService<ConduitTypeRegistry>();

        // Assert — self-published types are in it; the inbound filter, not the binding,
        // is what stops the app's own copy from running its listeners a second time
        registry.ActiveEventTypeNames.Should().Contain(typeof(ItemCreatedEvent).FullName!);
    }
}
