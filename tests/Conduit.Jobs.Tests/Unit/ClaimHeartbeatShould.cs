using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Unit;

[TestClass]
public class ClaimHeartbeatShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(2);

    [TestMethod]
    public async Task FenceItselfAfterClaimTimeoutOfFailures()
    {
        // Arrange — a claim taken at Start, renewed every thirty seconds against a database that has gone
        var timeProvider = new FakeTimeProvider(Start);
        var store = UnreachableJobStore.Create();
        using var jobCancellation = new CancellationTokenSource();
        await using var heartbeat = StartHeartbeat(store, timeProvider, jobCancellation);

        // Act — three renewals fail, then the clock runs on until the heartbeat gives the run up
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(1), () => Task.FromResult(store.Calls >= 3));
        var lostAfterThreeFailures = heartbeat.ClaimLost || jobCancellation.IsCancellationRequested;
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(1), () => Task.FromResult(jobCancellation.IsCancellationRequested));

        // Assert — no other host may take the claim before ClaimTimeout, so the job runs on until then;
        // from then on another host may, so this one must already have stopped
        lostAfterThreeFailures.Should().BeFalse("ninety seconds without a renewal is not yet a stale claim");
        heartbeat.ClaimLost.Should().BeTrue();
        timeProvider.GetUtcNow().Should().BeOnOrAfter(Start + ClaimTimeout)
            .And.BeBefore(Start + ClaimTimeout + HeartbeatInterval, "it fences at the timeout, not a beat later");
    }

    [TestMethod]
    public async Task FenceItselfWhenARenewalOutlastsTheClaimTimeout()
    {
        // Arrange — the first renewal hangs: a connection that stopped answering, not one that refused
        var timeProvider = new FakeTimeProvider(Start);
        var store = UnreachableJobStore.Create(hangs: true);
        using var jobCancellation = new CancellationTokenSource();
        await using var heartbeat = StartHeartbeat(store, timeProvider, jobCancellation);
        timeProvider.Advance(HeartbeatInterval);
        await store.WaitForCallsAsync(1);

        // Act — the clock reaches the moment another host may take the claim, with the renewal still out
        timeProvider.Advance(ClaimTimeout - HeartbeatInterval - TimeSpan.FromSeconds(1));
        var lostBeforeTheTimeout = heartbeat.ClaimLost || jobCancellation.IsCancellationRequested;
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await WaitForCancellationAsync(jobCancellation.Token);

        // Assert — the hung renewal cannot keep the job running past the claim timeout
        lostBeforeTheTimeout.Should().BeFalse();
        heartbeat.ClaimLost.Should().BeTrue();
        store.Calls.Should().Be(1);
    }

    private static ClaimHeartbeat StartHeartbeat(
        UnreachableJobStore store, FakeTimeProvider timeProvider, CancellationTokenSource jobCancellation)
    {
        var services = new ServiceCollection()
            .AddScoped(_ => store.Store)
            .BuildServiceProvider();
        var run = new JobRun
        {
            Id = 1,
            JobKey = "warm@2026-08-25T02:00:00.0000000Z",
            ScheduleName = "warm",
            MessageType = "Conduit.Jobs.Tests.Support.RecordedJob",
            Payload = "{}",
            RunAt = Start,
            Status = JobRunStatus.Claiming,
            ClaimedAt = Start,
            ClaimToken = "host-a",
            StartedAt = Start,
        };

        return ClaimHeartbeat.Start(
            run,
            maxRuntime: null,
            services.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            Options.Create(new JobsOptions { HeartbeatInterval = HeartbeatInterval, ClaimTimeout = ClaimTimeout }),
            jobCancellation,
            NullLogger.Instance);
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        throw new TimeoutException("The job's token was never cancelled.");
    }
}
