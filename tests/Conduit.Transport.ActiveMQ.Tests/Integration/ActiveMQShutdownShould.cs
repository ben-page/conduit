using System.Diagnostics;
using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.ActiveMQ.Tests.Support.Contracts;

namespace Conduit.Transport.ActiveMQ.Tests.Integration;

/// <summary>
/// Tests what stopping a host does to a handler that is still running: stop drains it before closing
/// the receiver link it accepts on, bounded by the host's shutdown deadline.
/// </summary>
[TestClass]
public class ActiveMQShutdownShould : ActiveMQTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.ActiveMQ.Tests.Support.Contracts";

    // A delivery left unsettled by a closed link is released and redelivered at once; the standby's
    // handler then runs 2 s
    private static readonly TimeSpan RedeliveryWindow = TimeSpan.FromSeconds(4);

    [TestMethod]
    [Timeout(90_000)]
    public async Task CompleteAnInFlightHandlerBeforeClosing()
    {
        // Arrange -- two instances of one application compete for the command; the one that takes it
        // is stopped mid-handler, and the other stays up to receive it should the broker redeliver it
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var firstHost = await CreateHostAsync(handlerName);
        var prefix = firstHost.TopologyPrefix!;
        await using var secondHost = await CreateHostAsync(handlerName, prefix);
        await using var senderHost = await CreateSenderHostAsync(senderName, prefix, ContractsNamespace, "broker");

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

        // Assert -- stop waited for the handler, its reply went out before the links closed, and the
        // message was acknowledged once: the standby instance never receives a redelivery
        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1));
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
}
