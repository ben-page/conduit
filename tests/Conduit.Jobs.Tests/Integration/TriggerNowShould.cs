using System.Text.Json;
using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// <see cref="IJobs.TriggerNowAsync"/> honours the schedule's overlap rule. The durable cases use a
/// manage-only host, so nothing claims the runs and the ledger holds exactly what each test put there.
/// </summary>
[TestClass]
public class TriggerNowShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RefuseWhileTheScheduleHasAPendingRun()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(database, WarmEveryFiveMinutes);
        await JobsDatabaseProbe.StoreFor(database).TryMaterializeAsync(SeedRun(), CancellationToken.None);

        // Act
        var act = () => host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);

        // Assert — typed and named, so a caller can turn it into its own refusal
        (await act.Should().ThrowAsync<JobOverlapException>())
            .Which.ScheduleName.Should().Be("warm");
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(1, "nothing was queued");
    }

    [TestMethod]
    public async Task RefuseWhileTheScheduleHasAClaimedRun()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(database, WarmEveryFiveMinutes);
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.TryMaterializeAsync(SeedRun(), CancellationToken.None);
        (await store.ClaimDueAsync("runner", Start, 10, CancellationToken.None)).Should().ContainSingle();

        // Act
        var act = () => host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<JobOverlapException>().WithMessage("*'warm'*");
    }

    [TestMethod]
    public async Task QueueARunWhenNothingIsOutstanding()
    {
        // Arrange — a terminal run is history, not outstanding work
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(database, WarmEveryFiveMinutes);
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.TryMaterializeAsync(SeedRun(), CancellationToken.None);
        var claimed = await store.ClaimDueAsync("runner", Start, 10, CancellationToken.None);
        await store.MarkTerminalAsync(
            claimed[0], claimed[0].ClaimToken!, JobRunStatus.Failed, "gave up", "warm", null, Start, CancellationToken.None);

        // Act
        var key = await host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);

        // Assert — the key it returns is the run it queued
        key.Should().Be(ManualRunKey.Build("warm", Start));
        var pending = await store.GetPendingAsync("warm", CancellationToken.None);
        pending.Should().ContainSingle().Which.JobKey.Should().Be(key);
    }

    [TestMethod]
    public async Task ReadTheQueuedRunBackByTheKeyItReturns()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(database, WarmEveryFiveMinutes);
        var jobs = host.GetRequiredService<IJobs>();

        // Act
        var key = await jobs.TriggerNowAsync("warm", CancellationToken.None);

        // Assert
        var run = await jobs.GetRunAsync(key, CancellationToken.None);
        run.Should().NotBeNull();
        run!.Status.Should().Be(JobRunStatus.Pending);
        run.ScheduleName.Should().Be("warm");
        run.IsManual.Should().BeTrue();
    }

    [TestMethod]
    public async Task QueueRegardlessWhenTheScheduleAllowsOverlap()
    {
        // Arrange — one run pending and one claimed
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(
            database,
            jobs => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).AllowOverlap().Run(() => new RecordedJob("warm")));
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.TryMaterializeAsync(SeedRun("warm@claimed"), CancellationToken.None);
        await store.ClaimDueAsync("runner", Start, 10, CancellationToken.None);
        await store.TryMaterializeAsync(SeedRun("warm@pending"), CancellationToken.None);

        // Act
        var key = await host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);

        // Assert
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(3);
        (await store.GetRunAsync(key, CancellationToken.None)).Should().NotBeNull("the key names the run it queued");
    }

    [TestMethod]
    public async Task RefuseASecondTriggerWhileTheFirstIsOutstanding()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(database, WarmEveryFiveMinutes, timeProvider);
        var jobs = host.GetRequiredService<IJobs>();
        await jobs.TriggerNowAsync("warm", CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Act
        var act = () => jobs.TriggerNowAsync("warm", CancellationToken.None);

        // Assert — an earlier manual run is as outstanding as an occurrence
        await act.Should().ThrowAsync<JobOverlapException>();
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(1);
    }

    [TestMethod]
    public async Task QueueExactlyOneOfManyConcurrentTriggers()
    {
        // Arrange — every trigger reads a different instant, so none collapse on a shared key
        var timeProvider = new FakeTimeProvider(Start) { AutoAdvanceAmount = TimeSpan.FromMilliseconds(1) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartManageOnlyHostAsync(database, WarmEveryFiveMinutes, timeProvider);
        var jobs = host.GetRequiredService<IJobs>();

        // Act — each on a thread of its own: Microsoft.Data.Sqlite's async methods run synchronously
        var attempts = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            try
            {
                await jobs.TriggerNowAsync("warm", CancellationToken.None);
                return true;
            }
            catch (JobOverlapException)
            {
                return false;
            }
        })));

        // Assert
        attempts.Count(queued => queued).Should().Be(1);
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(1);
    }

    [TestMethod]
    public async Task SkipAnOccurrenceWhileAManualRunIsOutstanding()
    {
        // Arrange — a manual run that is still running when the schedule's own occurrence comes due
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);
        await host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(30), () => Task.FromResult(spy.StartedCount > 0));

        // Act — step past the occurrence, letting the heartbeat keep the manual run's claim fresh
        var occurrence = Start.AddMinutes(5);
        while (timeProvider.GetUtcNow() <= occurrence.AddSeconds(30))
        {
            timeProvider.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(20);
        }

        await Task.Delay(300);

        // Assert — the scheduler treats a manual run exactly as it treats an earlier occurrence
        spy.StartedCount.Should().Be(1);
        (await JobsDatabaseProbe.StoreFor(database).GetRunAsync(
            DurableJobScheduler.BuildJobKey("warm", occurrence), CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task RefuseWhileTheScheduledRunIsInProgressWithoutAnEngine()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — bounded, because a trigger that wrongly ran would block on the frozen clock forever
        var act = () => host.GetRequiredService<IJobs>()
            .TriggerNowAsync("warm", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        // Assert — without a database "outstanding" means in progress on this host
        await act.Should().ThrowAsync<JobOverlapException>();
        spy.StartedCount.Should().Be(1);
    }

    [TestMethod]
    public async Task RunBesideTheScheduledRunWhenTheScheduleAllowsOverlapWithoutAnEngine()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).AllowOverlap().Run(() => new RecordedJob("warm")),
            spy,
            timeProvider);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act
        var trigger = host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);

        // Assert — both running at once, then the manual one finishes normally
        await spy.WaitForStartedAsync(2);
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await trigger.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task SkipAnOccurrenceWhileAManualRunIsInProgressWithoutAnEngine()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);
        var trigger = host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);
        await spy.WaitForStartedAsync(1);

        // Act — the occurrence comes due while the manual run is still going
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        // Assert
        spy.StartedCount.Should().Be(1);
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await trigger.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static Task<Conduit.TestUtils.TestHost> StartManageOnlyHostAsync(
        SharedSqliteDatabase database, Action<JobsBuilder> declare, FakeTimeProvider? timeProvider = null)
        => JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.ManageOnly = true;
                declare(jobs);
            },
            timeProvider: timeProvider ?? new FakeTimeProvider(Start),
            connectionString: database.ConnectionString);

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));

    private static JobRun SeedRun(string jobKey = "warm@seed") => new()
    {
        JobKey = jobKey,
        ScheduleName = "warm",
        Occurrence = null,
        MessageType = typeof(RecordedJob).FullName!,
        Payload = JsonSerializer.Serialize(new RecordedJob("warm"), ConduitRuntime.JsonOptions),
        RunAt = Start,
        Status = JobRunStatus.Pending,
    };
}
