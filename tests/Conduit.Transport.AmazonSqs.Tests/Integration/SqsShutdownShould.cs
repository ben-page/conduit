using System.Diagnostics;
using AwesomeAssertions;
using Conduit.TestUtils;
using Conduit.Transport.AmazonSqs.Tests.Support.Contracts;

namespace Conduit.Transport.AmazonSqs.Tests.Integration;

/// <summary>
/// Tests what stopping a host does to a handler that is still running: stop drains it before disposing
/// the SQS client it deletes the message with, bounded by the host's shutdown deadline.
/// </summary>
[TestClass]
public class SqsShutdownShould : SqsTestBase
{
    private const string ContractsNamespace = "Conduit.Transport.AmazonSqs.Tests.Support.Contracts";

    // An undeleted message reappears once its visibility timeout lapses; kept short so a redelivery
    // shows within the test. ResponseTimeout may not exceed it, so both are five seconds.
    private const int VisibilityTimeoutSeconds = 5;

    // The visibility timeout, the standby's 2 s handler, and a margin for its long poll
    private static readonly TimeSpan RedeliveryWindow = TimeSpan.FromSeconds(VisibilityTimeoutSeconds + 5);

    [TestMethod]
    [Timeout(90_000)]
    public async Task CompleteAnInFlightHandlerBeforeClosing()
    {
        // Arrange -- two instances of one application compete for the command; the one that takes it
        // is stopped mid-handler, and the other stays up to receive it should the broker redeliver it
        var handlerName = UniqueAppName("handler");
        var senderName = UniqueAppName("sender");

        await using var firstHost = await CreateHostAsync(handlerName, configureOptions: ShortVisibility);
        var prefix = firstHost.TopologyPrefix!;
        await using var secondHost = await CreateHostAsync(handlerName, prefix, configureOptions: ShortVisibility);
        await using var senderHost = await CreateSenderHostAsync(
            senderName, prefix, ContractsNamespace, "broker", ShortVisibility);

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

        // Assert -- stop waited for the handler, its reply went out before the client was disposed, and
        // the message was deleted once: the standby instance never receives a redelivery
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

    private static void ShortVisibility(AmazonSqsOptions options)
    {
        options.VisibilityTimeoutSeconds = VisibilityTimeoutSeconds;
        options.ResponseTimeout = TimeSpan.FromSeconds(VisibilityTimeoutSeconds);
    }
}
