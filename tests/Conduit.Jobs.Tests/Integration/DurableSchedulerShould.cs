using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class DurableSchedulerShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);
    // The first after Start: interval occurrences align to the Unix epoch, not to a host's start.
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task RunAnOccurrenceExactlyOnceAcrossThreeHosts()
    {
        // Arrange — three hosts sharing one database, as replicas of one application do
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var hostA = await StartHostAsync(database, timeProvider, spy);
        await using var hostB = await StartHostAsync(database, timeProvider, spy);
        await using var hostC = await StartHostAsync(database, timeProvider, spy);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Assert
        await spy.WaitForCountAsync(1);
        await Task.Delay(200);
        spy.Count.Should().Be(1, "the row claim elects exactly one host per occurrence");
    }

    [TestMethod]
    public async Task DeleteTheRunAndAdvanceTheCursorOnSuccess()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForCountAsync(1);

        // Assert — the runs table holds work, never history
        var jobKey = DurableJobScheduler.BuildJobKey("warm", Occurrence);
        await JobsDatabaseProbe.WaitForDeletionAsync(database, jobKey);
        var schedule = await JobsDatabaseProbe.StoreFor(database)
            .GetScheduleAsync("warm", CancellationToken.None);
        schedule!.LastCompletedOccurrence.Should().Be(Occurrence);
    }

    [TestMethod]
    public async Task NotReRunACompletedOccurrenceAfterItsRowIsDeleted()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForCountAsync(1);
        await JobsDatabaseProbe.WaitForDeletionAsync(
            database, DurableJobScheduler.BuildJobKey("warm", Occurrence));

        // Act — a second host starts and evaluates the same, already-completed occurrence
        await using var late = await StartHostAsync(database, timeProvider, spy);
        await Task.Delay(300);

        // Assert — the cursor, not the deleted row, is what prevents re-running it
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    [DataRow(false, DisplayName = "No overlap: the cursor is read under the schedule's lock")]
    [DataRow(true, DisplayName = "AllowOverlap: the cursor is read just before the insert")]
    public async Task NotReRunAnOccurrenceAnotherReplicaCompletedWhileThisOneWasLate(bool allowOverlap)
    {
        // Arrange — this host is running and waiting for the occurrence
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                var schedule = jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5));
                if (allowOverlap)
                {
                    schedule = schedule.AllowOverlap();
                }

                schedule.Run(() => new RecordedJob("warm"));
            },
            spy,
            timeProvider,
            database.ConnectionString);

        // ...when another replica, whose timer fired first, runs it to the end: its row is deleted and
        // the cursor advanced, in one transaction
        await JobsDatabaseProbe.StoreFor(database).AdvanceCursorAsync("warm", Occurrence, CancellationToken.None);

        // Act — this host's timer fires late
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        // Assert — the loop read the cursor only at startup; the check at materialization is what stops it
        spy.StartedCount.Should().Be(0);
        (await JobsDatabaseProbe.StoreFor(database).GetRunAsync(
            DurableJobScheduler.BuildJobKey("warm", Occurrence), CancellationToken.None)).Should().BeNull();
    }

    [TestMethod]
    public async Task AgreeOnIntervalOccurrencesAcrossHostsWithDifferentStartTimes()
    {
        // Arrange — three replicas that came up ten seconds apart, each reading its own clock at start;
        // anchored to its own start, each would run every interval at a time of its own
        var clocks = new[]
        {
            new FakeTimeProvider(Start),
            new FakeTimeProvider(Start.AddSeconds(10)),
            new FakeTimeProvider(Start.AddSeconds(20)),
        };
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var hostA = await StartHostAsync(database, clocks[0], spy);
        await using var hostB = await StartHostAsync(database, clocks[1], spy);
        await using var hostC = await StartHostAsync(database, clocks[2], spy);
        DateTimeOffset[] occurrences =
        [
            new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero),
            new(2026, 8, 25, 2, 5, 0, TimeSpan.Zero),
            new(2026, 8, 25, 2, 10, 0, TimeSpan.Zero),
        ];

        // Act — from here on the three read the same time, through three intervals
        await AdvanceTogetherAsync(clocks, occurrences[^1].AddSeconds(30));
        await spy.WaitForCountAsync(occurrences.Length);
        foreach (var occurrence in occurrences)
        {
            await JobsDatabaseProbe.WaitForDeletionAsync(database, DurableJobScheduler.BuildJobKey("warm", occurrence));
        }

        await Task.Delay(200);

        // Assert — one run per interval, each of the occurrence every replica computed
        spy.Count.Should().Be(occurrences.Length, "replicas agree on an interval's occurrences without a cursor");
        var history = await JobsDatabaseProbe.StoreFor(database).QueryHistoryAsync(
            new JobHistoryFilter { ScheduleName = "warm" }, CancellationToken.None);
        history.Entries.Select(entry => entry.JobKey).Order(StringComparer.Ordinal).Should().Equal(
            occurrences.Select(occurrence => DurableJobScheduler.BuildJobKey("warm", occurrence)));
    }

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);

    /// <summary>
    /// Sets every clock to the same instant, five seconds at a time, until <paramref name="until"/>. In
    /// steps, because the loops re-arm their timers asynchronously after each firing.
    /// </summary>
    private static async Task AdvanceTogetherAsync(IReadOnlyList<FakeTimeProvider> clocks, DateTimeOffset until)
    {
        var now = clocks.Max(clock => clock.GetUtcNow());
        while (now < until)
        {
            now += TimeSpan.FromSeconds(5);
            foreach (var clock in clocks)
            {
                clock.SetUtcNow(now);
            }

            await Task.Delay(20);
        }
    }

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
