using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Tests event routing over Amazon SQS: multicast delivery to all subscribing apps,
/// LocalOnly attribute suppression, and continue-on-failure semantics.
/// </summary>
/// <remarks>
/// Events use multicast subscriptions -- each app with a different ApplicationName
/// gets its own SNS subscription forwarding to its own SQS queue, so all subscribers
/// receive a copy.
/// </remarks>
[TestClass]
public class SqsEventRoutingShould : SqsTestBase
{
    [TestMethod]
    public async Task DeliverEventToAllSubscribingApps()
    {
        // Arrange
        var appNameA = UniqueAppName("app-a");
        var appNameB = UniqueAppName("app-b");
        var publisherName = UniqueAppName("pub");

        await using var hostA = await CreateHostAsync(appNameA);
        var prefix = hostA.TopologyPrefix!;
        await using var hostB = await CreateHostAsync(appNameB, prefix);
        await using var publisherHost = await CreateHostAsync(publisherName, prefix);

        var spyA = hostA.GetRequiredService<SpyLog>();
        var spyB = hostB.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act
        await mediator.PublishAsync(new TestEvent("broadcast"));

        // Assert -- both subscribing apps received the event
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

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(publisherName, prefix);

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var publisherSpy = publisherHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act -- publish a [LocalOnly] event from the publisher
        await mediator.PublishAsync(new LocalOnlyTestEvent("local-only"));

        // Assert -- publisher's local listener fires, but handler host does NOT receive it
        await publisherSpy.WaitForCountAsync(1);
        publisherSpy.Invocations.OfType<LocalOnlyTestEvent>().Should().Contain(
            e => e.Value == "local-only");

        // Give some time for any erroneous broker delivery
        await Task.Delay(2000);
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

        // Act -- ThrowingEventListener records then throws
        await mediator.PublishAsync(new ThrowingTestEvent("boom"));

        // Assert -- the listener recorded the event before throwing
        await spy.WaitForCountAsync(1);
        spy.Invocations.OfType<ThrowingTestEvent>().Should().Contain(e => e.Value == "boom");
    }

    [TestMethod]
    public async Task DeliverFireAndForgetEventToSubscribingApp()
    {
        // Arrange — [FireAndForget] only affects local listener execution (background),
        // transport broadcast should still deliver the event to subscribing apps
        var publisherName = UniqueAppName("pub");
        var listenerName = UniqueAppName("listener");

        await using var listenerHost = await CreateHostAsync(listenerName);
        var prefix = listenerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(publisherName, prefix);

        var listenerSpy = listenerHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(3000);

        // Act
        await mediator.PublishAsync(new FireAndForgetEvent("f&f-event"));

        // Assert
        await listenerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));
        listenerSpy.Invocations.OfType<FireAndForgetEvent>().Should().ContainSingle(
            e => e.Value == "f&f-event");
    }
}
