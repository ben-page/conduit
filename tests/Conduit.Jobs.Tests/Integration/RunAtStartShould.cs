using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// <c>RunAtStart()</c> on the durable scheduler: one run when a host's scheduler starts, written like a
/// manual run, so the cursor, the misfire walk and the schedule's phase are what they would have been
/// without it. Several hosts on one database stand in for the replicas of one application.
/// </summary>
[TestClass]
public class RunAtStartShould
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 1, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThreeDaysStale = new(2026, 10, 9, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MostRecentMissed = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RunOnceWhenTheHostStarts_ThenOnItsInterval()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();

        // Act
        await using var host = await StartAsync(database, timeProvider, spy, TimeSpan.FromMinutes(5));

        // Assert
        await spy.WaitForCountAsync(1);
        await Task.Delay(300);
        spy.Count.Should().Be(1, "one start, one start-up run");

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForCountAsync(2);
    }

    [TestMethod]
    public async Task RunOnceAfterAnOutage_NotOncePerMissedOccurrence_AndLeaveTheCursorToTheMisfireWalk()
    {
        // Arrange — an hourly schedule whose cursor is three days stale, as after a weekend outage
        await using var database = SharedSqliteDatabase.Create();
        await JobsDatabaseProbe.SeedCursorAsync(database, "warm", ThreeDaysStale);
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();

        // Act
        await using var host = await StartAsync(database, timeProvider, spy, TimeSpan.FromHours(1));

        // Assert
        await spy.WaitForCountAsync(1);
        await Task.Delay(300);
        spy.Count.Should().Be(1, "the Skip walk runs nothing and the start-up run is one run");
        var row = await JobsDatabaseProbe.StoreFor(database).GetScheduleAsync("warm", CancellationToken.None);
        row!.LastCompletedOccurrence.Should().Be(
            MostRecentMissed, "the start-up run is not an occurrence: the cursor is the walk's");
    }

    [TestMethod]
    public async Task WriteOneRun_ForTwoStartsAtTheSameInstant()
    {
        // Arrange — the first start's run lasts a minute, so it is still outstanding when the second
        // start writes. That is the whole of what the key promises: a completed schedule run's key is
        // not retained, so a write for the same start collapses into the first only while that run is
        // outstanding, as trigger-now's key does. AllowOverlap() takes the overlap lock out of it, so
        // only the shared key can collapse the second write.
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(1) };

        // Act — the clock does not move between the two starts
        await using var first = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutesAllowingOverlapAndAtStart, spy, timeProvider, database.ConnectionString);
        await spy.WaitForStartedAsync(1);
        await using var second = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutesAllowingOverlapAndAtStart, spy, timeProvider, database.ConnectionString);
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        // Assert
        await spy.WaitForCountAsync(1);
        await Task.Delay(300);
        spy.Count.Should().Be(1, "both starts compute one key");
    }

    [TestMethod]
    public async Task NotRunAtStartWhileAnotherRunIsStillOutstanding()
    {
        // Arrange — the first replica's start-up run is still running when the second replica starts
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var first = await StartAsync(database, timeProvider, spy, TimeSpan.FromHours(1));
        await spy.WaitForStartedAsync(1);

        // Act — a second replica, started a moment later
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await using var second = await StartAsync(database, timeProvider, spy, TimeSpan.FromHours(1));
        await Task.Delay(300);

        // Assert
        spy.StartedCount.Should().Be(
            1, "a no-overlap schedule has at most one run outstanding, a start-up run included");
    }

    [TestMethod]
    public async Task NotRunAtStartWhenPaused()
    {
        // Arrange — the shape of JobsApiShould.SurvivePauseAcrossARestart: pause through one host,
        // restart as another
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        var first = await StartAsync(database, timeProvider, spy, TimeSpan.FromHours(1));
        await spy.WaitForCountAsync(1);
        await first.GetRequiredService<IJobs>().PauseAsync("warm", CancellationToken.None);
        await first.DisposeAsync();

        // Act
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await using var second = await StartAsync(database, timeProvider, spy, TimeSpan.FromHours(1));
        await Task.Delay(300);

        // Assert
        spy.Count.Should().Be(1, "the paused schedule's second start writes no start-up run");
    }

    [TestMethod]
    public async Task NotRunAtStartWithoutTheOptIn()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        var spy = new JobSpy();

        // Act
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm")),
            spy,
            new FakeTimeProvider(Start),
            database.ConnectionString);
        await Task.Delay(300);

        // Assert
        spy.Count.Should().Be(0);
    }

    private static Task<Conduit.TestUtils.TestHost> StartAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy, TimeSpan every)
        => JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm").Every(every).RunAtStart().Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString);

    private static void WarmEveryFiveMinutesAllowingOverlapAndAtStart(JobsBuilder jobs)
        => jobs.Schedule("warm")
            .Every(TimeSpan.FromMinutes(5))
            .AllowOverlap()
            .RunAtStart()
            .Run(() => new RecordedJob("warm"));
}
