using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class ClaimedJobExecutorShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task WriteNothingWhenTheClaimWasLost()
    {
        // Arrange — a ten-second job, done well inside one heartbeat interval, so no renewal notices
        // what happens to its claim meanwhile
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromSeconds(10) };
        var logs = new SpyLoggerProvider();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutes,
            spy,
            timeProvider,
            database.ConnectionString,
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);
        var store = JobsDatabaseProbe.StoreFor(database);
        var running = (await store.GetInFlightAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        // Act — another host takes the claim over, as it may once the claim looks stale (this host
        // paused past ClaimTimeout, say), and then this host's job finishes
        var takenOver = await store.ClaimDueAsync(
            "host-b", timeProvider.GetUtcNow() + new JobsOptions().ClaimTimeout, 10, CancellationToken.None);
        timeProvider.Advance(spy.JobDuration);
        await spy.WaitForCountAsync(1);
        var claimLost = await WaitForEntryAsync(logs, entry => entry.EventId.Id == 410);

        // Assert — the run is the other host's now: no deletion, no cursor, no history from this one
        takenOver.Should().ContainSingle();
        claimLost.EventId.Name.Should().Be("ClaimLost");
        claimLost.Level.Should().Be(LogLevel.Warning);
        claimLost.Message.Should().Contain(running.JobKey);
        var run = await store.GetRunAsync(running.JobKey, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be("host-b");
        (await store.GetScheduleAsync("warm", CancellationToken.None))!.LastCompletedOccurrence.Should().BeNull();
        (await store.QueryHistoryAsync(new JobHistoryFilter { JobKey = running.JobKey }, CancellationToken.None))
            .Entries.Should().BeEmpty();
    }

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));

    private static async Task<SpyLogEntry> WaitForEntryAsync(SpyLoggerProvider logs, Func<SpyLogEntry, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (true)
        {
            var found = logs.Entries.FirstOrDefault(match);
            if (found is not null)
            {
                return found;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"No matching entry was logged. Saw: [{string.Join(" | ", logs.Entries.Select(entry => entry.Message))}]");
            }

            await Task.Delay(10);
        }
    }
}
