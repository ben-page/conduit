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
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 25, 2, 4, 0, TimeSpan.Zero);

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

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, JobSpy spy)
        => JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
