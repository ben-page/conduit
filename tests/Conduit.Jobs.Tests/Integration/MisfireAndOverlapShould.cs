using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class MisfireAndOverlapShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ThreeDaysStale = new(2026, 8, 22, 2, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset MostRecentMissed = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task SkipEveryMissedOccurrenceByDefault()
    {
        // Arrange — a cursor three days stale, as after a weekend outage
        await using var database = SharedSqliteDatabase.Create();
        await JobsDatabaseProbe.SeedCursorAsync(database, "nightly", ThreeDaysStale);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 3, 0, 0, TimeSpan.Zero));
        var spy = new JobSpy();

        // Act
        await using var host = await StartHostAsync(database, timeProvider, spy, MisfirePolicy.Skip);
        await Task.Delay(300);

        // Assert — three catch-up runs would send three of whatever this job sends
        spy.Count.Should().Be(0);
        var schedule = await JobsDatabaseProbe.StoreFor(database)
            .GetScheduleAsync("nightly", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(MostRecentMissed);
    }

    [TestMethod]
    public async Task RunExactlyOneCatchUpUnderRunImmediately()
    {
        // Arrange
        await using var database = SharedSqliteDatabase.Create();
        await JobsDatabaseProbe.SeedCursorAsync(database, "nightly", ThreeDaysStale);
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 3, 0, 0, TimeSpan.Zero));
        var spy = new JobSpy();

        // Act
        await using var host = await StartHostAsync(
            database, timeProvider, spy, MisfirePolicy.RunImmediately);

        // Assert — one, not three: there is no backfill
        await spy.WaitForCountAsync(1);
        await Task.Delay(300);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task SkipAnOccurrenceWhileThePreviousOneIsStillOutstanding()
    {
        // Arrange — a job taking far longer than its own interval
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm")
                .Every(TimeSpan.FromMinutes(1))
                .Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);
        await Task.Delay(300);

        // Assert
        spy.StartedCount.Should().Be(1, "overlap is skipped by default");
    }

    [TestMethod]
    public async Task StartConcurrentRunsWhenTheScheduleAllowsOverlap()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm")
                .Every(TimeSpan.FromMinutes(1))
                .AllowOverlap()
                .Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(3));

        // Assert
        await spy.WaitForStartedAsync(3);
        spy.StartedCount.Should().Be(3);
    }

    private static Task<Conduit.TestUtils.TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy, MisfirePolicy policy)
        => JobsTestHost.StartAsync(
            jobs => jobs.Schedule("nightly")
                .Cron("0 2 * * *")
                .OnMisfire(policy)
                .Run(() => new RecordedJob("nightly")),
            spy,
            timeProvider,
            database.ConnectionString);
}
