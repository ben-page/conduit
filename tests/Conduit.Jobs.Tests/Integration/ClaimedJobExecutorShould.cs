using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
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
        await using var host = await StartHostAsync(database, timeProvider, spy, logs);
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

    [TestMethod]
    public async Task LogTheClaimLostForARunTheHeartbeatGaveUp()
    {
        // Arrange — a ten-minute job, so its heartbeat renews while it runs
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        var logs = new SpyLoggerProvider();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy, logs);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);
        var store = JobsDatabaseProbe.StoreFor(database);
        var running = (await store.GetInFlightAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        // Act — another host takes the claim over, and this host's next renewal finds it gone
        await store.ClaimDueAsync(
            "host-b", timeProvider.GetUtcNow() + new JobsOptions().ClaimTimeout, 10, CancellationToken.None);
        timeProvider.Advance(new JobsOptions().HeartbeatInterval);
        var claimLost = await WaitForEntryAsync(logs, entry => entry.EventId.Id == 410);

        // Assert — the heartbeat stopped the job, and the executor recorded nothing for it
        claimLost.Message.Should().Contain(running.JobKey);
        logs.Entries.Should().Contain(entry => entry.Message.Contains("was taken by another host", StringComparison.Ordinal));
        spy.Count.Should().Be(0, "the job was cancelled before it finished");
        var run = await store.GetRunAsync(running.JobKey, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be("host-b");
        (await store.QueryHistoryAsync(new JobHistoryFilter { JobKey = running.JobKey }, CancellationToken.None))
            .Entries.Should().BeEmpty();
    }

    [TestMethod]
    [DataRow(1, DisplayName = "No attempts left: the run would be marked failed")]
    [DataRow(3, DisplayName = "Attempts left: the run would be released for a retry")]
    public async Task WriteNothingWhenAFailedAttemptsClaimWasLost(int maxAttempts)
    {
        // Arrange — a ten-second job that fails
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromSeconds(10), FailuresRemaining = 1 };
        var logs = new SpyLoggerProvider();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy, logs, maxAttempts);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);
        var store = JobsDatabaseProbe.StoreFor(database);
        var running = (await store.GetInFlightAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        // Act — another host takes the claim over, then this host's attempt fails
        await store.ClaimDueAsync(
            "host-b", timeProvider.GetUtcNow() + new JobsOptions().ClaimTimeout, 10, CancellationToken.None);
        timeProvider.Advance(spy.JobDuration);
        var claimLost = await WaitForEntryAsync(logs, entry => entry.EventId.Id == 410);

        // Assert — neither the exhaustion nor the retry is recorded or reported: the run is not this
        // host's to fail or to hand back to Pending
        claimLost.Message.Should().Contain(running.JobKey);
        logs.Entries.Should().NotContain(entry => entry.Message.Contains("failed on attempt", StringComparison.Ordinal));
        var run = await store.GetRunAsync(running.JobKey, CancellationToken.None);
        run!.Status.Should().Be(JobRunStatus.Claiming);
        run.ClaimToken.Should().Be("host-b");
        run.AttemptCount.Should().Be(0);
        run.LastError.Should().BeNull();
        (await store.QueryHistoryAsync(new JobHistoryFilter { JobKey = running.JobKey }, CancellationToken.None))
            .Entries.Should().BeEmpty();
    }

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database,
        FakeTimeProvider timeProvider,
        JobSpy spy,
        SpyLoggerProvider logs,
        int maxAttempts = 3)
        => JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.MaxAttempts = maxAttempts;
                WarmEveryFiveMinutes(jobs);
            },
            spy,
            timeProvider,
            database.ConnectionString,
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));

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
