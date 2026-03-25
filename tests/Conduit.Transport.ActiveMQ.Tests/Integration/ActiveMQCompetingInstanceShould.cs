using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests competing consumer behavior over ActiveMQ Artemis: multiple instances with the same
/// ApplicationName share a queue, so each message is delivered to exactly one instance.
/// For events, each unique ApplicationName gets its own queue on the multicast address,
/// so every app gets a copy but competing instances within the same app still deduplicate.
/// </summary>
[TestClass]
[DoNotParallelize]
public class ActiveMQCompetingInstanceShould
{
    private static ActiveMQFixture Fixture => AssemblySetup.Fixture;

    private const string ContractsNamespace = "Conduit.Transport.ActiveMQ.Tests.Support.Contracts";

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
    [DataRow(1)]
    [DataRow(20)]
    public async Task DeliverCommandToExactlyOneInstance(int messageCount)
    {
        // Arrange
        EnsureAvailable();

        var sharedHandlerName = TransportTestFixture.UniqueAppName("shared");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        // 3 handler instances with the SAME ApplicationName -> competing consumers
        var handler1 = await Fixture.CreateHostAsync(sharedHandlerName);
        var handler2 = await Fixture.CreateHostAsync(sharedHandlerName);
        var handler3 = await Fixture.CreateHostAsync(sharedHandlerName);

        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var spy1 = handler1.GetRequiredService<SpyLog>();
        var spy2 = handler2.GetRequiredService<SpyLog>();
        var spy3 = handler3.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        for (var i = 0; i < messageCount; i++)
        {
            await mediator.ExecuteAsync(new TestCommand($"msg-{i}"));
        }

        // Assert — total across all instances equals exactly messageCount (each handled once)
        await Task.Delay(500);

        var totalSpy = spy1.Count + spy2.Count + spy3.Count;
        totalSpy.Should().Be(messageCount,
            $"Expected exactly {messageCount} total invocations across 3 competing instances, got {totalSpy}");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(20)]
    public async Task DeliverQueryToExactlyOneInstance(int messageCount)
    {
        // Arrange
        EnsureAvailable();

        var sharedHandlerName = TransportTestFixture.UniqueAppName("shared");
        var senderName = TransportTestFixture.UniqueAppName("sender");

        // 3 handler instances with the SAME ApplicationName -> competing consumers
        var handler1 = await Fixture.CreateHostAsync(sharedHandlerName);
        var handler2 = await Fixture.CreateHostAsync(sharedHandlerName);
        var handler3 = await Fixture.CreateHostAsync(sharedHandlerName);

        var senderHost = await Fixture.CreateSenderHostAsync(senderName, ContractsNamespace, "broker");

        var spy1 = handler1.GetRequiredService<SpyLog>();
        var spy2 = handler2.GetRequiredService<SpyLog>();
        var spy3 = handler3.GetRequiredService<SpyLog>();
        var mediator = senderHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        for (var i = 0; i < messageCount; i++)
        {
            var result = await mediator.FetchAsync(new TestQuery($"query-{i}"));
            result.Value.Should().StartWith("result:");
        }

        // Assert — total across all instances equals exactly messageCount
        await Task.Delay(500);

        var totalSpy = spy1.Count + spy2.Count + spy3.Count;
        totalSpy.Should().Be(messageCount,
            $"Expected exactly {messageCount} total invocations across 3 competing instances, got {totalSpy}");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(20)]
    public async Task DeliverEventToExactlyOneInstancePerApp(int messageCount)
    {
        // Arrange
        EnsureAvailable();

        var appNameB = TransportTestFixture.UniqueAppName("app-b");
        var appNameC = TransportTestFixture.UniqueAppName("app-c");
        var publisherName = TransportTestFixture.UniqueAppName("pub");

        // 2 instances of AppB (same name) + 2 instances of AppC (same name)
        var hostB1 = await Fixture.CreateHostAsync(appNameB);
        var hostB2 = await Fixture.CreateHostAsync(appNameB);
        var hostC1 = await Fixture.CreateHostAsync(appNameC);
        var hostC2 = await Fixture.CreateHostAsync(appNameC);

        var publisherHost = await Fixture.CreateHostAsync(publisherName);

        var spyB1 = hostB1.GetRequiredService<SpyLog>();
        var spyB2 = hostB2.GetRequiredService<SpyLog>();
        var spyC1 = hostC1.GetRequiredService<SpyLog>();
        var spyC2 = hostC2.GetRequiredService<SpyLog>();
        var mediator = publisherHost.GetRequiredService<IMediator>();

        await Task.Delay(2000);

        // Act
        for (var i = 0; i < messageCount; i++)
        {
            await mediator.PublishAsync(new TestEvent($"event-{i}"));
        }

        // Assert — each app (B and C) received exactly messageCount total across its instances
        var totalB = spyB1.Count + spyB2.Count;
        var totalC = spyC1.Count + spyC2.Count;

        // Wait for delivery with some settling time
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while ((totalB < messageCount || totalC < messageCount) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            totalB = spyB1.Count + spyB2.Count;
            totalC = spyC1.Count + spyC2.Count;
        }

        totalB.Should().Be(messageCount,
            $"AppB should have received {messageCount} events total across 2 instances, got {totalB}");
        totalC.Should().Be(messageCount,
            $"AppC should have received {messageCount} events total across 2 instances, got {totalC}");
    }
}
