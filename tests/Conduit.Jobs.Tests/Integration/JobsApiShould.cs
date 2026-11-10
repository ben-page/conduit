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
        var run = await store.InsertClaimedAsync(
            new JobRun
            {
                JobKey = "warm@done",
                ScheduleName = "warm",
                MessageType = "Conduit.Jobs.Tests.Support.RecordedJob",
                Payload = "{}",
                RunAt = Start,
            },
            "worker",
            Start,
            CancellationToken.None);
        await store.MarkTerminalAsync(
            run!, "worker", JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Start,
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

        // Act — past the 02:00 occurrence and short of the next, then wait without moving the clock again: the
        // job fails at once. Stepping the clock until a failure showed up let a slow host reach 02:05 first, and the
        // second occurrence failed too
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await JobsDatabaseProbe.WaitForTerminalAsync(
            database, DurableJobScheduler.BuildJobKey("warm", new DateTimeOffset(2026, 8, 25, 2, 0, 0, TimeSpan.Zero)));

        // Assert
        var failed = await jobsApi.GetFailedAsync(JobFilter.All, CancellationToken.None);
        failed.Should().ContainSingle();
        failed[0].LastError.Should().Contain("on purpose");
        failed[0].ScheduleName.Should().Be("warm");
    }

    [TestMethod]
    public async Task ListHistoryForInspection()
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

        // Act — the same occurrence that lands in GetFailedAsync also lands in history, permanently. Waited
        // for without moving the clock again: past 02:05 the next occurrence would run and fail too
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await JobsDatabaseProbe.WaitForTerminalAsync(
            database, DurableJobScheduler.BuildJobKey("warm", Start.AddMinutes(1)));

        // Assert
        var history = await jobsApi.GetHistoryAsync(new JobHistoryFilter(), CancellationToken.None);
        var entry = history.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(JobRunOutcome.Failed);
        entry.LastError.Should().Contain("on purpose");
        entry.ScheduleName.Should().Be("warm");
        entry.IsManual.Should().BeFalse("an occurrence is not a manual run");
        entry.AttemptCount.Should().Be(0, "no attempt had failed when its one attempt began");
    }

    [TestMethod]
    public async Task FollowAManualRunFromTheLedgerIntoItsHistory()
    {
        // Arrange — a job that takes a minute: trigger-now wakes the sweep, so an instant one could be gone
        // from the ledger before the first read
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(1) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy);
        var jobs = host.GetRequiredService<IJobs>();
        var key = await jobs.TriggerNowAsync("warm", CancellationToken.None);
        (await jobs.GetRunAsync(key, CancellationToken.None)).Should().NotBeNull("the ledger holds it until it ends");

        // Act — a success deletes the ledger row in the transaction that writes the history row
        await FakeClock.AdvanceUntilAsync(
            timeProvider,
            TimeSpan.FromSeconds(30),
            async () => await jobs.GetRunAsync(key, CancellationToken.None) is null);

        // Assert
        var history = await jobs.GetHistoryAsync(new JobHistoryFilter { JobKey = key }, CancellationToken.None);
        var entry = history.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(JobRunOutcome.Completed);
        entry.ScheduleName.Should().Be("warm");
        entry.IsManual.Should().BeTrue();
        entry.AttemptCount.Should().Be(0, "a first-try success");
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task ReadAFailedRunByItsKey()
    {
        // Arrange — a terminal run's row is kept for inspection, so its key still reads it
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), new JobSpy());
        var store = JobsDatabaseProbe.StoreFor(database);
        var stored = await store.InsertClaimedAsync(
            new JobRun
            {
                JobKey = "warm@failed",
                ScheduleName = "warm",
                MessageType = "Conduit.Jobs.Tests.Support.RecordedJob",
                Payload = "{}",
                RunAt = Start,
            },
            "worker",
            Start,
            CancellationToken.None);
        await store.MarkTerminalAsync(
            stored!, "worker", JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null, Start,
            CancellationToken.None);

        // Act
        var run = await host.GetRequiredService<IJobs>().GetRunAsync("warm@failed", CancellationToken.None);

        // Assert
        run.Should().NotBeNull();
        run!.Status.Should().Be(JobRunStatus.Failed);
        run.LastError.Should().Be("boom");
        run.IsManual.Should().BeFalse();
    }

    [TestMethod]
    public async Task ReadNoRunForAnUnknownKey()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), new JobSpy());

        // Act
        var run = await host.GetRequiredService<IJobs>().GetRunAsync("nope@manual-x", CancellationToken.None);

        // Assert
        run.Should().BeNull();
    }

    [TestMethod]
    public async Task ReturnAManualRunsKeyButReadNoRunWithoutAnEngine()
    {
        // Arrange — the in-memory trigger runs inline, so the run is over when the key comes back
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);
        var jobs = host.GetRequiredService<IJobs>();

        // Act
        var key = await jobs.TriggerNowAsync("warm", CancellationToken.None);

        // Assert
        key.Should().Be(ManualRunKey.Build("warm", Start));
        spy.Count.Should().Be(1);
        (await jobs.GetRunAsync(key, CancellationToken.None)).Should().BeNull("there is no ledger");
    }

    [TestMethod]
    public async Task ReportTheNextOccurrenceOfEachDeclaredSchedule()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, new FakeTimeProvider(Start), new JobSpy());

        // Act
        var schedules = await host.GetRequiredService<IJobs>().GetSchedulesAsync(CancellationToken.None);

        // Assert — computed from the declaration, the same answer every host reaches: the first
        // occurrence aligned to the Unix epoch, not five minutes after this host started
        schedules.Should().ContainSingle();
        schedules[0].Name.Should().Be("warm");
        schedules[0].NextOccurrence.Should().Be(new DateTimeOffset(2026, 8, 25, 2, 0, 0, TimeSpan.Zero));
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
        };
        var stored = await store.InsertClaimedAsync(run, "worker", Start, CancellationToken.None);
        await store.IncrementAttemptAsync(
            stored!.Id, "worker", Start.AddMinutes(1), "boom", CancellationToken.None);

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

    [TestMethod]
    public async Task ReturnAnEmptyHistoryPageWithoutAnEngine()
    {
        // Arrange — InMemoryJobs has no ledger to have a history of, so empty is truthful, not a stub
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("rollup").Every(TimeSpan.FromHours(1)).Run(() => new NoopJob()));

        // Act
        var history = await host.GetRequiredService<IJobs>()
            .GetHistoryAsync(new JobHistoryFilter(), CancellationToken.None);

        // Assert
        history.Entries.Should().BeEmpty();
        history.TotalCount.Should().Be(0);
    }

    [TestMethod]
    public async Task ListAScopeParkedBehindAnInFlightRun()
    {
        // Arrange — for a projection, scope depth IS queue depth: every key that changed while a
        // rebuild runs parks a scope until it finishes
        var spy = new LocationSummarySpy { BlockUntilReleased = true };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.On<BadgeScanned>("rebuild")
                .CoalesceBy(scan => scan.LocationId)
                .Run(scan => new RebuildLocationSummary(scan.LocationId, scan.From))
                .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming),
            timeProvider: new FakeTimeProvider(Start),
            connectionString: database.ConnectionString,
            locationSummarySpy: spy);
        var mediator = host.GetRequiredService<IMediator>();

        await mediator.PublishAsync(new BadgeScanned("loc-1", Start.AddHours(9)));
        await spy.WaitForCountAsync(1);

        await mediator.PublishAsync(new BadgeScanned("loc-1", Start.AddHours(8)));
        await mediator.PublishAsync(new BadgeScanned("loc-1", Start.AddHours(7)));
        await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);

        // Act
        var scopes = await host.GetRequiredService<IJobs>()
            .GetPendingScopesAsync(JobFilter.All, CancellationToken.None);

        // Assert — Version climbing while DueAt holds still is the signal it is absorbing a stream
        var waiting = scopes.Should().ContainSingle().Subject;
        waiting.TriggerName.Should().Be("rebuild");
        waiting.CoalesceKey.Should().Be("loc-1");
        waiting.Version.Should().Be(1);

        spy.BlockUntilReleased = false;
        spy.ReleaseOne();
    }

    [TestMethod]
    public async Task ReturnNoScopesWithoutAnEngine()
    {
        // Arrange — event triggers are rejected at startup without an engine, so there are no scopes
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("rollup").Every(TimeSpan.FromHours(1)).Run(() => new NoopJob()));

        // Act
        var scopes = await host.GetRequiredService<IJobs>()
            .GetPendingScopesAsync(JobFilter.All, CancellationToken.None);

        // Assert
        scopes.Should().BeEmpty();
    }

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
