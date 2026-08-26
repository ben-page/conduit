using AwesomeAssertions;
using Conduit.Jobs;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class JobsApiShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NotStartFurtherOccurrencesWhilePaused()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy);

        // Act
        await host.GetRequiredService<IJobs>().PauseAsync("warm", CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        // Assert
        spy.Count.Should().Be(0);
    }

    [TestMethod]
    public async Task SurvivePauseAcrossARestart()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        var hostA = await StartHostAsync(database, timeProvider, spy);
        await hostA.GetRequiredService<IJobs>().PauseAsync("warm", CancellationToken.None);
        await hostA.DisposeAsync();

        // Act — a pause that only affected the host you called would be useless in a cluster
        await using var hostB = await StartHostAsync(database, timeProvider, spy);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        // Assert
        spy.Count.Should().Be(0);
        var schedules = await hostB.GetRequiredService<IJobs>().GetSchedulesAsync(CancellationToken.None);
        schedules.Should().ContainSingle(schedule => schedule.Name == "warm" && schedule.IsPaused);
    }

    [TestMethod]
    public async Task ResumeAPausedSchedule()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy);
        var jobs = host.GetRequiredService<IJobs>();
        await jobs.PauseAsync("warm", CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(200);

        // Act
        await jobs.ResumeAsync("warm", CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Assert
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task RunAScheduleOnDemandWithoutDisturbingItsCursor()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy);

        // Act
        await host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(30), () => Task.FromResult(spy.Count > 0));

        // Assert — the occurrence the schedule was already waiting for must still happen
        spy.Count.Should().BeGreaterThan(0);
        var schedule = await JobsDatabaseProbe.StoreFor(database)
            .GetScheduleAsync("warm", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().BeNull("a manual run is not an occurrence");
    }

    [TestMethod]
    public async Task ReportNotFoundWhenCancellingAnUnknownJob()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), new JobSpy());

        // Act
        var outcome = await host.GetRequiredService<IJobs>()
            .CancelAsync("nope@2026-01-01T00:00:00Z", CancellationToken.None);

        // Assert — the caller must confront the ambiguity rather than assume success
        outcome.Should().Be(CancellationOutcome.NotFound);
    }

    [TestMethod]
    public async Task ReportAlreadyCompleteWhenCancellingATerminalRun()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), new JobSpy());
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.TryMaterializeAsync(
            new JobRun
            {
                JobKey = "warm@done",
                ScheduleName = "warm",
                MessageType = "Conduit.Jobs.Tests.Support.RecordedJob",
                Payload = "{}",
                RunAt = Start,
                Status = JobRunStatus.Pending,
            },
            CancellationToken.None);
        var run = await store.GetRunAsync("warm@done", CancellationToken.None);
        await store.MarkTerminalAsync(
            run!.Id, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null,
            CancellationToken.None);

        // Act
        var outcome = await host.GetRequiredService<IJobs>()
            .CancelAsync("warm@done", CancellationToken.None);

        // Assert — distinct from NotFound, so an operator can tell 'never existed' from 'too late'
        outcome.Should().Be(CancellationOutcome.AlreadyComplete);
    }

    [TestMethod]
    public async Task ListFailedRunsForInspection()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { FailuresRemaining = int.MaxValue };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.MaxAttempts = 1;
                jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
            },
            spy,
            timeProvider,
            database.ConnectionString);
        var jobsApi = host.GetRequiredService<IJobs>();

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await FakeClock.AdvanceUntilAsync(
            timeProvider,
            TimeSpan.FromMinutes(1),
            async () => (await jobsApi.GetFailedAsync(JobFilter.All, CancellationToken.None)).Count > 0);

        // Assert
        var failed = await jobsApi.GetFailedAsync(JobFilter.All, CancellationToken.None);
        failed.Should().ContainSingle();
        failed[0].LastError.Should().Contain("on purpose");
        failed[0].ScheduleName.Should().Be("warm");
    }

    [TestMethod]
    public async Task ReportTheNextOccurrenceOfEachDeclaredSchedule()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), new JobSpy());

        // Act
        var schedules = await host.GetRequiredService<IJobs>().GetSchedulesAsync(CancellationToken.None);

        // Assert — computed from the declaration, the same answer every host reaches
        schedules.Should().ContainSingle();
        schedules[0].Name.Should().Be("warm");
        schedules[0].NextOccurrence.Should().Be(Start.AddMinutes(5));
        schedules[0].IsPaused.Should().BeFalse();
    }

    [TestMethod]
    public async Task DegradeToPerHostControlWithoutAPersistenceEngine()
    {
        // Arrange — no connection string, so no ledger to coordinate through
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);
        var jobs = host.GetRequiredService<IJobs>();

        // Act
        await jobs.PauseAsync("warm", CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        // Assert — pause still works here, it just cannot reach another host or survive a restart
        spy.Count.Should().Be(0);
        (await jobs.GetInFlightAsync(CancellationToken.None)).Should().BeEmpty("there is no ledger");
        (await jobs.GetSchedulesAsync(CancellationToken.None)).Should()
            .ContainSingle(schedule => schedule.Name == "warm" && schedule.IsPaused);
    }

    [TestMethod]
    public async Task ListARunAwaitingItsNextAttempt()
    {
        // Arrange — the sharpest blind spot: between attempts a run is Pending, so it appears in
        // neither GetInFlightAsync nor GetFailedAsync, and looks to an operator like nothing happened
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        var run = new JobRun
        {
            JobKey = "rollup@2026-08-26T02:00:00.0000000Z",
            ScheduleName = "rollup",
            MessageType = "Tests.RecordedJob",
            Payload = """{"label":"job"}""",
            RunAt = Start,
            Status = JobRunStatus.Pending,
        };
        await store.TryMaterializeAsync(run, CancellationToken.None);
        var stored = await store.GetRunAsync(run.JobKey, CancellationToken.None);
        await store.IncrementAttemptAsync(
            stored!.Id, Start.AddMinutes(1), "boom", CancellationToken.None);

        // Act
        var pending = await store.GetPendingAsync(null, CancellationToken.None);

        // Assert
        var awaiting = pending.Should().ContainSingle().Subject;
        awaiting.JobKey.Should().Be(run.JobKey);
        awaiting.AttemptCount.Should().Be(1);
        awaiting.LastError.Should().Be("boom");
        awaiting.RunAt.Should().Be(Start.AddMinutes(1), "RunAt is when the next attempt is due");
    }

    [TestMethod]
    public async Task ReturnNoPendingRunsWithoutAnEngine()
    {
        // Arrange — InMemoryJobs has no ledger to be pending in, so empty is truthful, not a stub
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("rollup").Every(TimeSpan.FromHours(1)).Run(() => new NoopJob()));

        // Act
        var pending = await host.GetRequiredService<IJobs>()
            .GetPendingAsync(JobFilter.All, CancellationToken.None);

        // Assert
        pending.Should().BeEmpty();
    }

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
