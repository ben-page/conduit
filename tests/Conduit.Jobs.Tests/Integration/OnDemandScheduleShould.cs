using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// An <c>OnDemand()</c> schedule has no timetable: neither scheduler ever starts it, and
/// <see cref="IJobs.TriggerNowAsync"/> runs it exactly as it runs a manual run of any schedule. Each
/// "never runs" test declares an hourly schedule beside it as the control — the control runs, so the
/// scheduler was live and the assertion about the on-demand schedule could have failed.
/// </summary>
[TestClass]
public class OnDemandScheduleShould
{
    private const string OnDemand = "rederive";
    private const string Control = "warm";

    // One interval before an occurrence: interval occurrences align to the Unix epoch.
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 1, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan SeveralDays = TimeSpan.FromDays(3);

    [TestMethod]
    public async Task NeverRunOnItsOwnAcrossSeveralDays()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            OnDemandBesideAnHourlyControl, spy, timeProvider, database.ConnectionString);

        // Act — hour by hour, so the control's loop and the claim sweep each wake many times over
        await AdvanceHourByHourAsync(timeProvider, SeveralDays);

        // Assert
        await spy.WaitForCountAsync(1);
        spy.Started.Should().Contain(Control, "the control proves the scheduler was running");
        await AssertNeverRanDurablyAsync(host, database, spy);
    }

    [TestMethod]
    public async Task NotRunOnARestart_NorCatchUpAMisfire()
    {
        // Arrange — a cursor three days stale, as a schedule once declared with a timetable leaves it,
        // and RunImmediately declared: on a cron schedule that would run one catch-up at this start
        var staleCursor = Start - SeveralDays;
        await using var database = SharedSqliteDatabase.Create();
        await JobsDatabaseProbe.SeedCursorAsync(database, OnDemand, staleCursor);
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        var first = await JobsTestHost.StartAsync(
            OnDemandCatchingUpBesideAnHourlyControl, spy, timeProvider, database.ConnectionString);
        await AdvanceHourByHourAsync(timeProvider, TimeSpan.FromHours(6));
        await spy.WaitForCountAsync(1);
        await first.DisposeAsync();
        var controlRunsBeforeRestart = spy.Count;

        // Act — the host comes back after a day down
        timeProvider.Advance(TimeSpan.FromDays(1));
        await using var second = await JobsTestHost.StartAsync(
            OnDemandCatchingUpBesideAnHourlyControl, spy, timeProvider, database.ConnectionString);
        await AdvanceHourByHourAsync(timeProvider, TimeSpan.FromHours(6));

        // Assert — the restarted host ran the control, and nothing for the on-demand schedule: no
        // misfire walk read or moved its cursor, and no catch-up was written
        await spy.WaitForCountAsync(controlRunsBeforeRestart + 1);
        await AssertNeverRanDurablyAsync(second, database, spy);
        var row = await JobsDatabaseProbe.StoreFor(database).GetScheduleAsync(OnDemand, CancellationToken.None);
        row!.LastCompletedOccurrence.Should().Be(staleCursor);
    }

    [TestMethod]
    public async Task RunOnceWhenTriggered()
    {
        // Arrange — declared alone, so the host's readiness rests on the sweep and nothing else
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => DeclareOnDemand(jobs), spy, timeProvider, database.ConnectionString);
        var jobsApi = host.GetRequiredService<IJobs>();

        // Act — the sweep claims the manual run on its next pass
        await jobsApi.TriggerNowAsync(OnDemand, CancellationToken.None);
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(30), () => Task.FromResult(spy.Count > 0));
        await AdvanceHourByHourAsync(timeProvider, TimeSpan.FromDays(1));

        // Assert — once, recorded in history as any manual run is, and the cursor never moved
        spy.Count.Should().Be(1);
        var history = await jobsApi.GetHistoryAsync(
            new JobHistoryFilter { ScheduleName = OnDemand }, CancellationToken.None);
        var entry = history.Entries.Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(JobRunOutcome.Completed);
        entry.JobKey.Should().StartWith($"{OnDemand}@manual-");
        var schedule = (await jobsApi.GetSchedulesAsync(CancellationToken.None)).Should().ContainSingle().Subject;
        schedule.LastCompletedOccurrence.Should().BeNull("a manual run is not an occurrence");
        schedule.NextOccurrence.Should().BeNull();
    }

    [TestMethod]
    [DataRow(false, DisplayName = "No overlap: the second trigger is refused")]
    [DataRow(true, DisplayName = "AllowOverlap: the second trigger is queued beside the first")]
    public async Task RefuseASecondTriggerWhileTheFirstIsRunning_UnlessItAllowsOverlap(bool allowOverlap)
    {
        // Arrange — the first manual run is claimed and running
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => DeclareOnDemand(jobs, allowOverlap), spy, timeProvider, database.ConnectionString);
        var jobsApi = host.GetRequiredService<IJobs>();
        await jobsApi.TriggerNowAsync(OnDemand, CancellationToken.None);
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(30), () => Task.FromResult(spy.StartedCount > 0));

        // Act
        var act = () => jobsApi.TriggerNowAsync(OnDemand, CancellationToken.None);

        // Assert — the overlap lock every schedule's manual run goes through
        if (allowOverlap)
        {
            await act.Should().NotThrowAsync();
            (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(2, "the running one and the queued one");
        }
        else
        {
            (await act.Should().ThrowAsync<JobOverlapException>()).Which.ScheduleName.Should().Be(OnDemand);
            (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(1, "nothing was queued");
        }
    }

    [TestMethod]
    public async Task RunWhenTriggeredWhilePaused_AsTriggerNowDoesForAnySchedule()
    {
        // Arrange — trigger-now does not consult pause: an operator asked for this run
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => DeclareOnDemand(jobs), spy, timeProvider, database.ConnectionString);
        var jobsApi = host.GetRequiredService<IJobs>();
        await jobsApi.PauseAsync(OnDemand, CancellationToken.None);

        // Act
        await jobsApi.TriggerNowAsync(OnDemand, CancellationToken.None);
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(30), () => Task.FromResult(spy.Count > 0));

        // Assert
        spy.Count.Should().Be(1);
        (await jobsApi.GetSchedulesAsync(CancellationToken.None)).Should().ContainSingle()
            .Which.IsPaused.Should().BeTrue();
    }

    [TestMethod]
    [DataRow(true, DisplayName = "Durable")]
    [DataRow(false, DisplayName = "Without an engine")]
    public async Task ListItAsOnDemandWithNoNextOccurrence(bool durable)
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            OnDemandBesideAnHourlyControl,
            new JobSpy(),
            new FakeTimeProvider(Start),
            durable ? database.ConnectionString : null);

        // Act
        var schedules = await host.GetRequiredService<IJobs>().GetSchedulesAsync(CancellationToken.None);

        // Assert — beside a timed schedule, which keeps its next occurrence and is not marked
        schedules.Should().HaveCount(2);
        var onDemand = schedules.Single(schedule => schedule.Name == OnDemand);
        onDemand.IsOnDemand.Should().BeTrue();
        onDemand.NextOccurrence.Should().BeNull();
        onDemand.LastCompletedOccurrence.Should().BeNull();
        var control = schedules.Single(schedule => schedule.Name == Control);
        control.IsOnDemand.Should().BeFalse();
        control.NextOccurrence.Should().Be(Start.AddHours(1));
    }

    [TestMethod]
    public async Task NeverRunOnItsOwnAcrossSeveralDaysWithoutAnEngine()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var host = await JobsTestHost.StartAsync(OnDemandBesideAnHourlyControl, spy, timeProvider);

        // Act
        await AdvanceHourByHourAsync(timeProvider, SeveralDays);

        // Assert
        await spy.WaitForCountAsync(1);
        spy.Started.Should().Contain(Control, "the control proves the scheduler was running");
        spy.Started.Should().NotContain(OnDemand);
    }

    [TestMethod]
    public async Task NotRunOnARestartWithoutAnEngine()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        var first = await JobsTestHost.StartAsync(OnDemandBesideAnHourlyControl, spy, timeProvider);
        await AdvanceHourByHourAsync(timeProvider, TimeSpan.FromHours(6));
        await spy.WaitForCountAsync(1);
        await first.DisposeAsync();
        var controlRunsBeforeRestart = spy.Count;

        // Act
        timeProvider.Advance(TimeSpan.FromDays(1));
        await using var second = await JobsTestHost.StartAsync(OnDemandBesideAnHourlyControl, spy, timeProvider);
        await AdvanceHourByHourAsync(timeProvider, TimeSpan.FromHours(6));

        // Assert
        await spy.WaitForCountAsync(controlRunsBeforeRestart + 1);
        spy.Started.Should().NotContain(OnDemand);
    }

    [TestMethod]
    public async Task RunOnceInlineWhenTriggeredWithoutAnEngine()
    {
        // Arrange — declared alone, so the host is ready with no loop at all
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var host = await JobsTestHost.StartAsync(jobs => DeclareOnDemand(jobs), spy, timeProvider);

        // Act — inline: the call returns when the run has finished
        await host.GetRequiredService<IJobs>().TriggerNowAsync(OnDemand, CancellationToken.None);
        var countWhenTheCallReturned = spy.Count;
        await AdvanceHourByHourAsync(timeProvider, TimeSpan.FromDays(1));

        // Assert
        countWhenTheCallReturned.Should().Be(1);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task RefuseASecondTriggerWhileTheFirstIsRunningWithoutAnEngine()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var host = await JobsTestHost.StartAsync(jobs => DeclareOnDemand(jobs), spy, timeProvider);
        var jobsApi = host.GetRequiredService<IJobs>();
        var first = jobsApi.TriggerNowAsync(OnDemand, CancellationToken.None);
        await spy.WaitForStartedAsync(1);

        // Act — bounded, because a trigger that wrongly ran would block on the frozen clock forever
        var act = () => jobsApi.TriggerNowAsync(OnDemand, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        // Assert
        await act.Should().ThrowAsync<JobOverlapException>();
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        spy.StartedCount.Should().Be(1);
    }

    [TestMethod]
    public async Task NotWarnThatItRunsOnEveryHostWithoutAnEngine()
    {
        // Arrange — the warning is for a schedule that fires on its own on every host; this one fires
        // on none, and a manual run executes only on the host it was triggered on
        var logs = new CapturingLoggerProvider();

        // Act
        await using var host = await JobsTestHost.StartAsync(
            OnDemandBesideAnHourlyControl, timeProvider: new FakeTimeProvider(Start), logs: logs);

        // Assert
        await logs.WaitForWarningAsync(entry => entry.Contains($"'{Control}'", StringComparison.Ordinal));
        logs.Warnings.Should().ContainSingle()
            .Which.Should().NotContain(OnDemand);
    }

    private static async Task AssertNeverRanDurablyAsync(TestHost host, SharedSqliteDatabase database, JobSpy spy)
    {
        spy.Started.Should().NotContain(OnDemand);

        var jobsApi = host.GetRequiredService<IJobs>();
        var history = await jobsApi.GetHistoryAsync(
            new JobHistoryFilter { ScheduleName = OnDemand }, CancellationToken.None);
        history.Entries.Should().BeEmpty("no run of it ever finished");
        (await jobsApi.GetInFlightAsync(CancellationToken.None))
            .Should().NotContain(run => run.ScheduleName == OnDemand);
        (await JobsDatabaseProbe.StoreFor(database).GetPendingAsync(OnDemand, CancellationToken.None))
            .Should().BeEmpty("no due run was ever written for it");
    }

    private static async Task AdvanceHourByHourAsync(FakeTimeProvider clock, TimeSpan span)
    {
        var until = clock.GetUtcNow() + span;
        while (clock.GetUtcNow() < until)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await Task.Delay(20);
        }
    }

    private static void DeclareOnDemand(JobsBuilder jobs, bool allowOverlap = false)
    {
        var schedule = jobs.Schedule(OnDemand).OnDemand();
        if (allowOverlap)
        {
            schedule = schedule.AllowOverlap();
        }

        schedule.Run(() => new RecordedJob(OnDemand));
    }

    private static void OnDemandBesideAnHourlyControl(JobsBuilder jobs)
    {
        DeclareOnDemand(jobs);
        jobs.Schedule(Control).Every(TimeSpan.FromHours(1)).Run(() => new RecordedJob(Control));
    }

    private static void OnDemandCatchingUpBesideAnHourlyControl(JobsBuilder jobs)
    {
        jobs.Schedule(OnDemand).OnDemand().OnMisfire(MisfirePolicy.RunImmediately).Run(() => new RecordedJob(OnDemand));
        jobs.Schedule(Control).Every(TimeSpan.FromHours(1)).Run(() => new RecordedJob(Control));
    }
}
