using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class JobFailureShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);
    private static readonly string JobKey = DurableJobScheduler.BuildJobKey("nightly", Occurrence);

    [TestMethod]
    public async Task RetryAFailedRunWithBackoffAndSurviveARestart()
    {
        // Arrange — the handler fails once, then would succeed
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { FailuresRemaining = 1 };
        await using var database = SharedSqliteDatabase.Create();
        var hostA = await StartHostAsync(database, timeProvider, spy);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var failed = await JobsDatabaseProbe.WaitForRunAsync(
            database, JobKey, run => run.AttemptCount == 1);
        failed.Status.Should().Be(JobRunStatus.Pending, "an attempt remains");
        failed.RunAt.Should().BeAfter(Occurrence, "the retry is backed off, not immediate");

        // Act — the process restarts before the retry is due
        await hostA.DisposeAsync();
        await using var hostB = await StartHostAsync(database, timeProvider, spy);

        // Assert — durable retry is the layer an in-process policy cannot provide
        await FakeClock.AdvanceUntilAsync(
            timeProvider,
            TimeSpan.FromMinutes(1),
            async () => await ReadRunAsync(database) is null);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task DelayTheRetryByTheConfiguredBackoff()
    {
        // Arrange — a handler that fails once, under a backoff that is no default
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { FailuresRemaining = 1 };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.RetryBackoff = [TimeSpan.FromMinutes(7)];
                jobs.Schedule("nightly").Cron("0 2 * * *").Run(() => new RecordedJob("nightly"));
            },
            spy,
            timeProvider,
            database.ConnectionString);

        // Act — the occurrence runs and fails at 02:00
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var failed = await JobsDatabaseProbe.WaitForRunAsync(database, JobKey, run => run.AttemptCount == 1);

        // Assert
        failed.RunAt.Should().Be(Occurrence.AddMinutes(7), "the first retry waits RetryBackoff[0]");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task FailAStoredRunWhoseCommandIsFireAndForget()
    {
        // Arrange — a pending run naming a [FireAndForget] command, as a run written before its type gained the
        // attribute would; dispatched, it would be recorded Completed before its handler had started
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.TryMaterializeAsync(
            new JobRun
            {
                JobKey = "nightly@stored",
                ScheduleName = "nightly",
                MessageType = typeof(FireAndForgetJob).FullName!,
                Payload = "{}",
                RunAt = Start,
                Status = JobRunStatus.Pending,
            },
            CancellationToken.None);

        // Act — a runner's sweep claims it at start
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.MaxAttempts = 1;
                jobs.Schedule("nightly").Cron("0 2 * * *").Run(() => new RecordedJob("nightly"));
            },
            spy,
            timeProvider,
            database.ConnectionString);
        var run = await JobsDatabaseProbe.WaitForTerminalAsync(database, "nightly@stored");

        // Assert — the attempt failed naming the reason, and the handler was never reached
        run.Status.Should().Be(JobRunStatus.Failed);
        run.LastError.Should().Contain(typeof(FireAndForgetJob).FullName!).And.Contain("[FireAndForget]");
        spy.StartedCount.Should().Be(0);
    }

    [TestMethod]
    public async Task MarkFailedAndAdvanceTheCursorOnceAttemptsAreExhausted()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { FailuresRemaining = int.MaxValue };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.MaxAttempts = 2;
                jobs.Schedule("nightly").Cron("0 2 * * *").Run(() => new RecordedJob("nightly"));
            },
            spy,
            timeProvider,
            database.ConnectionString);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await FakeClock.AdvanceUntilAsync(
            timeProvider,
            TimeSpan.FromMinutes(1),
            async () => await ReadRunAsync(database) is { Status: JobRunStatus.Failed });

        // Assert — one bad occurrence must not block the schedule forever
        var run = await ReadRunAsync(database);
        run!.AttemptCount.Should().Be(1, "the second attempt was terminal, so it was never counted as pending");
        run.LastError.Should().Contain("on purpose");
        var schedule = await JobsDatabaseProbe.StoreFor(database)
            .GetScheduleAsync("nightly", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Occurrence);
    }

    [TestMethod]
    public async Task NeverRetryACancelledRun()
    {
        // Arrange — a long-running job with a MaxRuntime it will breach
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromHours(1) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("nightly")
                .Cron("0 2 * * *")
                .MaxRuntime(TimeSpan.FromMinutes(1))
                .Run(() => new RecordedJob("nightly")),
            spy,
            timeProvider,
            database.ConnectionString);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await spy.WaitForStartedAsync(1);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(2));
        await JobsDatabaseProbe.WaitForTerminalAsync(database, JobKey);

        // Assert — retrying a cancelled job would defeat the cancellation
        await FakeClock.AdvanceUntilAsync(
            timeProvider,
            TimeSpan.FromMinutes(5),
            () => Task.FromResult(timeProvider.GetUtcNow() > Occurrence.AddMinutes(30)));
        var run = await ReadRunAsync(database);
        run!.Status.Should().Be(JobRunStatus.Cancelled);
        run.AttemptCount.Should().Be(0);
        spy.StartedCount.Should().Be(1, "the cancelled occurrence is never picked up again");
    }

    private static Task<JobRun?> ReadRunAsync(SharedSqliteDatabase database)
        => JobsDatabaseProbe.StoreFor(database).GetRunAsync(JobKey, CancellationToken.None);

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(NightlyAtTwo, spy, timeProvider, database.ConnectionString);

    private static void NightlyAtTwo(JobsBuilder jobs)
        => jobs.Schedule("nightly").Cron("0 2 * * *").Run(() => new RecordedJob("nightly"));
}
