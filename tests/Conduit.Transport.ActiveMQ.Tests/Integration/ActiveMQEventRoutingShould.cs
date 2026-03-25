using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
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
[DoNotParallelize]
public class ActiveMQEventRoutingShould
{
    private static ActiveMQFixture Fixture => AssemblySetup.Fixture;

    [TestCleanup]
    public async Task TestCleanup()
    {
        await Fixture.DisposeHostsAsync();
    }

    private static void EnsureAvailable()
    {
        if (!Fixture.IsAvailable)
        {
            Assert.Inconclusive("ActiveMQ Artemis container not available. Ensure Docker/Podman is running.");
        }
    }

    [TestMethod]
    public async Task DeliverEventToAllSubscribingApps()
    {
        // Arrange
        EnsureAvailable();

        var appNameA = TransportTestFixture.UniqueAppName("app-a");
        var appNameB = TransportTestFixture.UniqueAppName("app-b");
        var publisherName = TransportTestFixture.UniqueAppName("pub");

        var hostA = await Fixture.CreateHostAsync(appNameA);
        var hostB = await Fixture.CreateHostAsync(appNameB);
        var publisherHost = await Fixture.CreateHostAsync(publisherName);

        var spyA = hostA.GetRequiredService<SpyLog>();
        var spyB = hostB.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

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
        EnsureAvailable();

        var handlerName = TransportTestFixture.UniqueAppName("handler");
        var publisherName = TransportTestFixture.UniqueAppName("pub");

        var handlerHost = await Fixture.CreateHostAsync(handlerName);
        var publisherHost = await Fixture.CreateHostAsync(publisherName);

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var publisherSpy = publisherHost.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

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
        EnsureAvailable();

        var appName = TransportTestFixture.UniqueAppName("app");

        var host = await Fixture.CreateHostAsync(appName);

        var spy = host.GetRequiredService<SpyLog>();
        var mediator = host.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act — ThrowingEventListener records then throws
        await mediator.PublishAsync(new ThrowingTestEvent("boom"));

        // Assert — the listener recorded the event before throwing
        await spy.WaitForCountAsync(1);
        spy.Invocations.OfType<ThrowingTestEvent>().Should().Contain(e => e.Value == "boom");
    }
}
