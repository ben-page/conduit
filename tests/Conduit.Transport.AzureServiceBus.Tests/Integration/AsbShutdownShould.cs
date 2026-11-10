using System.Diagnostics;
using AwesomeAssertions;
using Azure.Messaging.ServiceBus;
using Conduit.Configuration;
using Conduit.Runtime;
using Conduit.TestUtils;
using Conduit.Transport.AzureServiceBus.Tests.Support.Contracts;

namespace Conduit.Transport.AzureServiceBus.Tests.Integration;

/// <summary>
/// Tests what stopping a host does to a handler that is still running: stop drains it before closing
/// the processor it settles on, bounded by the host's shutdown deadline; and to a request still
/// waiting for its reply.
/// </summary>
[TestClass]
public class AsbShutdownShould : AsbTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AzureServiceBus.Tests.Support.Contracts";

    // Service Bus redelivers an abandoned message at once, but one whose settle merely failed only when
    // its lock expires (a minute by default): peeking the queue is the sharper evidence on this broker
    private static readonly TimeSpan RedeliveryWindow = TimeSpan.FromSeconds(4);

    [TestMethod]
    [Timeout(90_000)]
    public async Task SettleTheInFlightMessageOnStop()
    {
        // Arrange -- two instances of one application compete for the command; the one that takes it
        // is stopped mid-handler, and the other stays up to receive it should the broker redeliver it
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var firstHost = await CreateHostAsync(handlerName);
        var prefix = firstHost.TopologyPrefix!;
        await using var secondHost = await CreateHostAsync(handlerName, prefix);
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(15));

        // Peeking reads a message whatever its lock, so it sees one whose settle failed
        var appQueueName = TopologyNaming.CreateAppResourceName(firstHost.GetRequiredService<ConduitOptions>(), 260);
        await using var client = new ServiceBusClient(ConnectionString);
        await using var queuePeeker = client.CreateReceiver(appQueueName);

        TestHost[] hosts = [firstHost, secondHost];
        SpyLog[] spies = [firstHost.GetRequiredService<SpyLog>(), secondHost.GetRequiredService<SpyLog>()];
        var reply = senderHost.GetRequiredService<IMediator>().ExecuteAsync(new ShutdownProbeCommand("drain", DelayMs: 2000));

        var running = await SpyLog.WaitForFirstAsync<ShutdownProbeCommand>(TimeSpan.FromSeconds(30), spies);
        var standbySpy = spies[1 - running];

        // Act -- stop that instance half a second into its two-second handler, as SIGTERM would
        await Task.Delay(500);
        var stopwatch = Stopwatch.StartNew();
        await hosts[running].DisposeAsync();
        stopwatch.Stop();

        // Assert -- stop waited for the handler and completed its message before closing the processor:
        // the queue no longer holds it, where a message whose settle failed stays, locked, until its
        // lock expires and the broker redelivers it
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
        (await queuePeeker.PeekMessageAsync()).Should().BeNull("stop completes the drained handler's message");

        // The reply went out too, and the message was handled once: the standby never receives it
        (await reply).Value.Should().Be("completed:drain");

        await Task.Delay(RedeliveryWindow);
        spies[running].Invocations.OfType<ShutdownProbeCommand>().Should().ContainSingle();
        standbySpy.Invocations.OfType<ShutdownProbeCommand>().Should().BeEmpty();
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task StopWhenTheHostTokenExpires()
    {
        // Arrange -- a handler that ignores cancellation and outlasts the host's shutdown deadline
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(5));

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var reply = senderHost.GetRequiredService<IMediator>().ExecuteAsync(new ShutdownProbeCommand("stuck", DelayMs: 10_000));
        await handlerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(30));

        // Act -- the host gives its services one second to stop
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();
        await handlerHost.StopAsync(shutdown.Token);
        stopwatch.Stop();

        // Assert -- stop waited for the handler until the deadline, then returned without it
        stopwatch.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));

        // The stopped instance never answered
        await reply.Awaiting(task => task).Should().ThrowExactlyAsync<TimeoutException>();
    }

    [TestMethod]
    [Timeout(90_000)]
    public async Task FailARequestStillAwaitingItsReplyOnStop()
    {
        // Arrange -- a request whose handler is still running, from a sender with time to spare
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var handlerHost = await CreateHostAsync(handlerName);
        var prefix = handlerHost.TopologyPrefix!;
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker",
            options => options.ResponseTimeout = TimeSpan.FromSeconds(30));

        var handlerSpy = handlerHost.GetRequiredService<SpyLog>();
        var reply = senderHost.GetRequiredService<IMediator>().ExecuteAsync(new ShutdownProbeCommand("orphaned", DelayMs: 5000));
        await handlerSpy.WaitForCountAsync(1, TimeSpan.FromSeconds(30));

        // Act -- the sender stops while it waits in the reply's session
        var stopwatch = Stopwatch.StartNew();
        await senderHost.StopAsync(CancellationToken.None);

        // Assert -- the request fails at once, neither answered later nor left to its response timeout
        await reply.Awaiting(task => task).Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("The transport for endpoint 'broker' stopped before the reply arrived.");
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }
}
