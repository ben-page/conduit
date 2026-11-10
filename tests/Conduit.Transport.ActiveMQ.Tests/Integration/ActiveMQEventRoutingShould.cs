using AwesomeAssertions;
using Conduit.Configuration;
using Conduit.Exceptions;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Conduit.Transport.ActiveMQ.Tests.Support.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests event routing over ActiveMQ Artemis: multicast delivery to all subscribing apps,
/// LocalOnly attribute suppression, and continue-on-failure semantics.
/// </summary>
/// <remarks>
/// Events use multicast subscriptions — each app with a different ApplicationName
/// gets its own queue on the multicast address, so all subscribers receive a copy.
/// </remarks>
[TestClass]
public class ActiveMQEventRoutingShould : ActiveMQTestBase
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

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(publisherName, prefix);

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var publisherSpy = publisherHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        // Act — publish a [LocalOnly] event from the publisher
        await mediator.PublishAsync(new LocalOnlyTestEvent("local-only"));

        // Assert — publisher's local listener fires, but handler host does NOT receive it
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

        // Act — ThrowingEventListener records then throws
        Func<Task> act = () => mediator.PublishAsync(new ThrowingTestEvent("boom"));

        // Assert — every listener ran, then PublishAsync surfaced the failure
        await act.Should().ThrowAsync<EventListenerException>();
        // and the listener recorded the event before throwing
        await spy.WaitForCountAsync(1);
        spy.Invocations.OfType<ThrowingTestEvent>().Should().Contain(e => e.Value == "boom");
    }

    [TestMethod]
    public async Task NotDeliverAnotherAppsEventTypes()
    {
        // Arrange — an application that listens for TestEvent alone, and a publisher of two event types
        var listenerName = UniqueAppName("listener");
        var publisherName = UniqueAppName("pub");

        await using var listenerHost = await CreateHostAsync(
            listenerName,
            configureHandling: handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>());
        var prefix = listenerHost.TopologyPrefix!;
        await using var publisherHost = await CreateHostAsync(
            publisherName, prefix, handling => handling.ClearAllHandlers());

        var listenerSpy = listenerHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();
        var appEventQueueName = TopologyNaming.CreateAppResourceName(
            listenerHost.GetRequiredService<ConduitOptions>(), 256);

        // Act — the event the application does not listen for goes first, over the same link
        await mediator.PublishAsync(new FireAndForgetEvent("not-mine"));
        await mediator.PublishAsync(new TestEvent("mine"));
        await listenerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));

        // Assert — the broker routed the application's queue the one type it subscribes, never the other
        await using var management = await ArtemisManagement.ConnectAsync(ConnectionString!);
        (await management.MessagesAddedAsync(appEventQueueName)).Should().Be(1);
        listenerSpy.Invocations.Should().ContainSingle().Which.Should().Be(new TestEvent("mine"));
    }

    [TestMethod]
    public async Task DeliverAnEventTypeAddedSinceTheQueueWasCreated()
    {
        // Arrange — a first release listened for TestEvent alone and has stopped; its event queue stays
        var listenerName = UniqueAppName("listener");
        string prefix;
        await using (var firstRelease = await CreateHostAsync(
            listenerName,
            configureHandling: handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>()))
        {
            prefix = firstRelease.TopologyPrefix!;
        }

        // Act — the next release, same name and prefix, also listens for FireAndForgetEvent
        await using var secondRelease = await CreateHostAsync(
            listenerName,
            prefix,
            handling => handling
                .ClearAllHandlers()
                .HandleEvent<TestEvent, TestEventListener>()
                .HandleEvent<FireAndForgetEvent, FireAndForgetEventListener>());
        await using var publisherHost = await CreateHostAsync(
            UniqueAppName("pub"), prefix, handling => handling.ClearAllHandlers());
        await publisherHost.GetRequiredService<IMediator>().PublishAsync(new FireAndForgetEvent("added"));

        // Assert — the existing queue's filter was updated to take the added type, which reached the listener
        var listenerSpy = secondRelease.GetRequiredService<SpyLog>();
        await listenerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));
        listenerSpy.Invocations.Should().ContainSingle().Which.Should().Be(new FireAndForgetEvent("added"));

        var appEventQueueName = TopologyNaming.CreateAppResourceName(
            secondRelease.GetRequiredService<ConduitOptions>(), 256);
        await using var management = await ArtemisManagement.ConnectAsync(ConnectionString!);
        (await management.FilterAsync(appEventQueueName)).Should()
            .Contain($"'{typeof(TestEvent).FullName}'")
            .And.Contain($"'{typeof(FireAndForgetEvent).FullName}'");
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

        // Act
        await mediator.PublishAsync(new FireAndForgetEvent("f&f-event"));

        // Assert
        await listenerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(15));
        listenerSpy.Invocations.OfType<FireAndForgetEvent>().Should().ContainSingle(
            e => e.Value == "f&f-event");
    }
}
