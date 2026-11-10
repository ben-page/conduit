using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class ClaimRenewalShould
{
    // One interval before an occurrence: interval occurrences align to the Unix epoch.
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 55, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly string JobKey = DurableJobScheduler.BuildJobKey("warm", Occurrence);

    [TestMethod]
    public async Task NotReClaimALongRunningJobWhileItsHostHeartbeats()
    {
        // Arrange — a job that runs far longer than the two-minute ClaimTimeout
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var database = SharedSqliteDatabase.Create();
        await using var hostA = await StartHostAsync(database, timeProvider, spy);
        await using var hostB = await StartHostAsync(database, timeProvider, spy);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — keep the clock moving well past ClaimTimeout, letting each renewal land so the other
        // host's sweep is never racing an unwritten heartbeat
        for (var beat = 0; beat < 6; beat++)
        {
            timeProvider.Advance(TimeSpan.FromSeconds(30));
            await JobsDatabaseProbe.WaitForClaimRenewedAsync(
                database, JobKey, timeProvider.GetUtcNow());
        }

        // Assert — the regression test for inheriting the outbox's five-minute constant: without
        // renewal the claim would look abandoned and the second host would run the job again
        spy.StartedCount.Should().Be(1, "a heartbeating host keeps its claim");
    }

    [TestMethod]
    public async Task ReleaseTheClaimOnShutdownSoAnotherHostResumesImmediately()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var database = SharedSqliteDatabase.Create();
        var hostA = await StartHostAsync(database, timeProvider, spy);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — drain host A mid-job
        await hostA.DisposeAsync();

        // Assert — the claim is handed back at once rather than left to expire, and the drain is not
        // counted as a failed attempt
        var released = await JobsDatabaseProbe.WaitForRunAsync(
            database, JobKey, run => run.Status == JobRunStatus.Pending);
        released.AttemptCount.Should().Be(0, "shutdown is a handover, not a failed attempt");
        released.ClaimToken.Should().BeNull();

        spy.JobDuration = TimeSpan.Zero;
        await using var hostB = await StartHostAsync(database, timeProvider, spy);
        await JobsDatabaseProbe.WaitForDeletionAsync(database, JobKey);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task NotStartARunOnceTheHostIsStopping()
    {
        // Arrange — another service takes half a second to stop, holding open the window in which the host
        // is stopping (ApplicationStopping fired) but the scheduler's own StopAsync has not yet run
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        var calls = new JobStoreCalls();
        await using var database = SharedSqliteDatabase.Create();
        var hostA = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutes,
            spy,
            timeProvider,
            database.ConnectionString,
            configureServices: services =>
            {
                CountingJobStore.Install(services, calls);
                services.AddHostedService(
                    provider => new StoppingHook(provider, _ => Task.Delay(TimeSpan.FromMilliseconds(500))));
            });
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — drain host A mid-job
        await hostA.DisposeAsync();

        // Assert — the drained run is handed back once, not re-claimed by a host on its way out: each re-claim
        // restarted the handler under a cancelled token, or handed the run straight back, hundreds of times
        calls.Count(nameof(IJobStore.ReleaseAsync)).Should().Be(1, "a stopping host takes no new work");
        spy.StartedCount.Should().Be(1);
        var released = await JobsDatabaseProbe.WaitForRunAsync(
            database, JobKey, run => run.Status == JobRunStatus.Pending);
        released.AttemptCount.Should().Be(0);
    }

    [TestMethod]
    public async Task HandBackATriggeredRunWithoutDispatchingItOnceTheHostIsStopping()
    {
        // Arrange — an event arrives while the host is stopping (a broker drains its in-flight deliveries
        // after ApplicationStopping), and its trigger writes and starts a claimed run
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        var host = await JobsTestHost.StartAsync(
            jobs => jobs.On<ThingHappened>().Run(thing => new RecordedJob(thing.Label)),
            spy,
            timeProvider,
            database.ConnectionString,
            configureServices: services => services.AddHostedService(
                provider => new StoppingHook(
                    provider,
                    services => services.GetRequiredService<IMediator>().PublishAsync(new ThingHappened("late")))));

        // Act
        await host.DisposeAsync();

        // Assert — the handler is never started under a token already cancelled; the run is left pending, so
        // another host runs it in full
        spy.StartedCount.Should().Be(0, "a stopping host dispatches no new run");
        var run = await JobsDatabaseProbe.WaitForOnlyRunAsync(database);
        run.Status.Should().Be(JobRunStatus.Pending);
        run.ClaimToken.Should().BeNull();
        run.AttemptCount.Should().Be(0);
    }

    [TestMethod]
    public async Task CancelAJobThatExceedsItsMaxRuntime()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm")
                .Every(TimeSpan.FromMinutes(5))
                .MaxRuntime(TimeSpan.FromMinutes(1))
                .Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — push past the one-minute ceiling while the ten-minute job is still running
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Assert — a hung job becomes a bounded, visible outcome instead of blocking its schedule
        var run = await JobsDatabaseProbe.WaitForTerminalAsync(database, JobKey);
        run.Status.Should().Be(JobRunStatus.Cancelled);
        spy.Count.Should().Be(0, "the handler never got to finish");
    }

    [TestMethod]
    public async Task AdvanceTheCursorWhenARunIsCancelled()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm")
                .Every(TimeSpan.FromMinutes(5))
                .MaxRuntime(TimeSpan.FromMinutes(1))
                .Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await JobsDatabaseProbe.WaitForTerminalAsync(database, JobKey);

        // Assert — without this the occurrence is re-materialized and cancelling loops forever
        var schedule = await JobsDatabaseProbe.StoreFor(database)
            .GetScheduleAsync("warm", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Occurrence);
    }

    [TestMethod]
    public async Task LetAJobThatIgnoresItsTokenCompleteAndRecordItAsCompleted()
    {
        // Arrange — a handler that never checks its CancellationToken, with a MaxRuntime it breaches
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(2), IgnoresCancellation = true };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm")
                .Every(TimeSpan.FromMinutes(5))
                .MaxRuntime(TimeSpan.FromMinutes(1))
                .Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — cancel it by breaching MaxRuntime, then let its own duration elapse
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Assert — cancellation is cooperative, so the work finishes and is recorded as a success,
        // not as a cancellation. "Cancelled" never means "did not run".
        await spy.WaitForCountAsync(1);
        await JobsDatabaseProbe.WaitForDeletionAsync(database, JobKey);
        var schedule = await JobsDatabaseProbe.StoreFor(database)
            .GetScheduleAsync("warm", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Occurrence);
    }

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
