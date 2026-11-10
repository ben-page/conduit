using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class DurableJobsShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    [Timeout(30_000)]
    public async Task NotRunAPendingRunThatWasCancelled()
    {
        // Arrange — a manual run queued and cancelled from a host that runs nothing, so it is still
        // pending, with its cancellation flag set, when a runner first sees it
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var manager = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.ManageOnly = true;
                WarmEveryFiveMinutes(jobs);
            },
            spy,
            timeProvider,
            database.ConnectionString);
        var jobs = manager.GetRequiredService<IJobs>();
        var key = await jobs.TriggerNowAsync("warm", CancellationToken.None);
        (await jobs.CancelAsync(key, CancellationToken.None)).Should().Be(CancellationOutcome.Requested);

        // Act — a runner's sweep claims it
        await using var runner = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, spy, timeProvider, database.ConnectionString);
        var run = await JobsDatabaseProbe.WaitForTerminalAsync(database, key);

        // Assert — marked cancelled without its command ever being dispatched, and recorded as such
        run.Status.Should().Be(JobRunStatus.Cancelled);
        spy.StartedCount.Should().Be(0, "an operator cancelled the run before it started");
        var history = await jobs.GetHistoryAsync(new JobHistoryFilter { JobKey = key }, CancellationToken.None);
        history.Entries.Should().ContainSingle().Which.Outcome.Should().Be(JobRunOutcome.Cancelled);
    }

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
