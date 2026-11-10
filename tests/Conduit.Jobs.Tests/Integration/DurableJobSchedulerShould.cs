using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>The durable scheduler's lifecycle: how promptly it takes work, and how it stops.</summary>
[TestClass]
public class DurableJobSchedulerShould
{
    // One interval before an occurrence: interval occurrences align to the Unix epoch.
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 55, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Occurrence = new(2026, 8, 25, 2, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [Timeout(30_000)]
    public async Task StopWhenTheShutdownTokenExpires()
    {
        // Arrange — a ten-minute job whose handler never checks its token, so cancelling it on the
        // host's stop does not end it
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10), IgnoresCancellation = true };
        var logs = new SpyLoggerProvider();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy, logs);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);

        // Act — the host's shutdown deadline passes while the job is still running
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await host.StopAsync(shutdown.Token);

        // Assert — the stop returned rather than wait for the job, and said which run it left behind
        var abandoned = logs.Entries.Should().ContainSingle(entry => entry.EventId.Id == 411).Subject;
        abandoned.EventId.Name.Should().Be("DrainAbandoned");
        abandoned.Level.Should().Be(LogLevel.Warning);
        abandoned.Message.Should().Contain(DurableJobScheduler.BuildJobKey("warm", Occurrence));
        spy.Count.Should().Be(0, "the job is still running");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RunATriggeredJobWithoutWaitingForTheSweep()
    {
        // Arrange — nothing is due, so the sweep is asleep for its full ClaimTimeout; the clock is
        // frozen, so it would never wake on its own
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartHostAsync(database, timeProvider, spy, new SpyLoggerProvider());

        // Act
        await host.GetRequiredService<IJobs>().TriggerNowAsync("warm", CancellationToken.None);

        // Assert — "run now" means now, not on the sweep's next pass
        await spy.WaitForCountAsync(1);
        timeProvider.GetUtcNow().Should().Be(Start, "the clock never moved");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task RunADelayedTriggerWhenItComesDueRatherThanOnTheSweepsNextPass()
    {
        // Arrange — a thirty-second delay, well inside the two minutes the idle sweep sleeps for
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.On<CartAbandoned>().After(TimeSpan.FromSeconds(30)).Run(cart => new RecordedJob(cart.CartId)),
            spy,
            timeProvider,
            database.ConnectionString);
        await host.GetRequiredService<IMediator>().PublishAsync(new CartAbandoned("cart-1"));
        await JobsDatabaseProbe.WaitForOnlyRunAsync(database);

        // Act — the clock reaches the run's due time, and no further
        await AdvanceInStepsAsync(timeProvider, TimeSpan.FromSeconds(30));

        // Assert
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task PickUpADelayedScopeWhenItComesDueRatherThanOnTheSweepsNextPass()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        using var summaries = new LocationSummarySpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.On<BadgeScanned>("rebuild")
                .After(TimeSpan.FromSeconds(30))
                .CoalesceBy(scan => scan.LocationId)
                .Run(scan => new RebuildLocationSummary(scan.LocationId, scan.From))
                .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming),
            timeProvider: timeProvider,
            connectionString: database.ConnectionString,
            locationSummarySpy: summaries);
        await host.GetRequiredService<IMediator>().PublishAsync(new BadgeScanned("loc-1", Start));
        await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);

        // Act — the clock reaches the scope's due time, and no further
        await AdvanceInStepsAsync(timeProvider, TimeSpan.FromSeconds(30));

        // Assert
        await summaries.WaitForCountAsync(1);
    }

    /// <summary>
    /// Moves the clock to <paramref name="total"/> in five-second steps. The publish's wake may still be running
    /// its sweep pass: one jump to the due time, landing between the pass's claim and its sleep computation,
    /// would leave the run due but unclaimed and the sweep asleep for its two-minute ceiling on a frozen clock.
    /// Stepping keeps the due time in the future until the sweep has armed its timer for it.
    /// </summary>
    private static async Task AdvanceInStepsAsync(FakeTimeProvider timeProvider, TimeSpan total)
    {
        var step = TimeSpan.FromSeconds(5);
        for (var advanced = TimeSpan.Zero; advanced < total; advanced += step)
        {
            timeProvider.Advance(step);
            await Task.Delay(20);
        }
    }

    private static Task<TestHost> StartHostAsync(
        SharedSqliteDatabase database,
        FakeTimeProvider timeProvider,
        JobSpy spy,
        SpyLoggerProvider logs)
        => JobsTestHost.StartAsync(
            WarmEveryFiveMinutes,
            spy,
            timeProvider,
            database.ConnectionString,
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));
}
