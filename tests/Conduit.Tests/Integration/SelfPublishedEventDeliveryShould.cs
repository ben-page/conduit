using AwesomeAssertions;
using Conduit.Generated;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Tests.Integration;

/// <summary>
/// TEMPORARY VERIFICATION — not part of the intended suite.
/// Proves whether a self-published event whose type is bound to the publishing app's own
/// event queue executes local listeners twice: once inline in PublishAsync, and again when
/// the broker returns the app's own copy through the wire-inbound path.
/// </summary>
[TestClass]
public class SelfPublishedEventDeliveryShould
{
    [TestMethod]
    public async Task RunLocalListenersTwiceWhenBrokerReturnsTheAppsOwnCopy()
    {
        // Arrange
        var spyTransport = new SpyTransport();
        await using var host = TestWebHost.Start(builder =>
        {
            builder.Services.AddSingleton<ItemStore>();
            builder.Services.AddSingleton<EventSpy<ItemCreatedEvent>>();
            builder.Services.AddSingleton<IConduitTransport>(spyTransport);
            builder.Services.AddConduit();
        });
        var mediator = host.GetRequiredService<IMediator>();
        var wireMediator = host.GetRequiredService<IWireMediator>();
        var spy = host.GetRequiredService<EventSpy<ItemCreatedEvent>>();

        // Act — one publish from this app
        await mediator.PublishAsync(new ItemCreatedEvent(Guid.NewGuid(), "SelfPublished"));
        await spy.WaitForCountAsync(1);

        var inlineCount = spy.Count;
        var broadcast = spyTransport.SentEvents.Single();

        // The app queue is bound to ConduitTypeRegistry.ActiveEventTypeNames — every discovered
        // event type, including ones this app publishes itself. So the broker routes this app's
        // own message back to it, and a consumer feeds it to the wire-inbound path.
        await wireMediator.HandleWireEventAsync(broadcast, CancellationToken.None);
        await spy.WaitForCountAsync(2);

        // Assert
        inlineCount.Should().Be(1, "PublishAsync runs local listeners inline on the publisher");
        spy.Count.Should().Be(2, "the app's own broadcast copy runs the same listeners a second time");
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

        // Assert — self-published types are in it; nothing filters them out
        registry.ActiveEventTypeNames.Should().Contain(typeof(ItemCreatedEvent).FullName!);
    }
}
