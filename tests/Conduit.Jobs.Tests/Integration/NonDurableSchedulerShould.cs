using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class NonDurableSchedulerShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DispatchTheCommandWhenAnOccurrenceIsDue()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Assert
        await spy.WaitForCountAsync(1);
        spy.Count.Should().Be(1);
    }

    [TestMethod]
    public async Task RunOnEveryHostBecauseThereIsNoCoordination()
    {
        // Arrange — two hosts, one schedule, no persistence
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var hostA = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);
        await using var hostB = await JobsTestHost.StartAsync(WarmEveryFiveMinutes, spy, timeProvider);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));

        // Assert — the documented behavior, pinned so it cannot regress into silent single-execution
        await spy.WaitForCountAsync(2);
        spy.Count.Should().Be(2);
    }

    [TestMethod]
    public async Task WarnAtStartupThatEachScheduleRunsOnEveryHost()
    {
        // Arrange
        var logs = new CapturingLoggerProvider();

        // Act
        await using var host = await JobsTestHost.StartAsync(
            WarmEveryFiveMinutes, timeProvider: new FakeTimeProvider(Start), logs: logs);

        // Assert — the guarantee is weaker here, so it must be visible in any real deployment's logs
        await logs.WaitForWarningAsync(entry =>
            entry.Contains("warm", StringComparison.Ordinal)
            && entry.Contains("every host", StringComparison.Ordinal));
        logs.Warnings.Should().ContainSingle();
    }

    [TestMethod]
    public async Task RejectAMisfirePolicyBecauseAnInMemoryTimerCannotKnowWhatItMissed()
    {
        // Arrange
        static void Configure(JobsBuilder jobs) => jobs.Schedule("report")
            .Cron("0 2 * * *")
            .OnMisfire(MisfirePolicy.RunImmediately)
            .Run(() => new RecordedJob("report"));

        // Act
        var act = async () =>
        {
            await using var host = await JobsTestHost.StartAsync(
                Configure, timeProvider: new FakeTimeProvider(Start));
        };

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*persistence engine*");
    }

    [TestMethod]
    public async Task RejectAnEventTriggerBecauseTriggersAreAlwaysDurable()
    {
        // Arrange
        static void Configure(JobsBuilder jobs)
            => jobs.On<ThingHappened>().Run(thing => new RecordedJob(thing.Label));

        // Act
        var act = async () =>
        {
            await using var host = await JobsTestHost.StartAsync(
                Configure, timeProvider: new FakeTimeProvider(Start));
        };

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*always durable*");
    }

    [TestMethod]
    public async Task CancelAJobThatExceedsItsMaxRuntimeWithoutAnEngine()
    {
        // Arrange — MaxRuntime needs only a start time and a token, so it works with no database
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromHours(1) };
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm")
                .Every(TimeSpan.FromMinutes(5))
                .MaxRuntime(TimeSpan.FromMinutes(1))
                .Run(() => new RecordedJob("warm")),
            spy,
            timeProvider);

        // Act
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForStartedAsync(1);
        timeProvider.Advance(TimeSpan.FromMinutes(2));

        // Assert — the handler is cancelled rather than running out its hour
        await Task.Delay(200);
        spy.Count.Should().Be(0);
        spy.StartedCount.Should().Be(1);
    }

    [TestMethod]
    public async Task RunOnceAtStart_WhenOptedIn()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();

        // Act
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutesAndAtStart, spy, timeProvider);

        // Assert — one run at start, then the timetable from the start instant
        await spy.WaitForCountAsync(1);
        await Task.Delay(300);
        spy.Count.Should().Be(1, "one start, one start-up run");

        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForCountAsync(2);
        spy.Count.Should().Be(2);
    }

    [TestMethod]
    public async Task BeReadyWhileTheStartUpRunIsStillRunning()
    {
        // Arrange — a start-up run far longer than JobsTestHost's ten-second wait for readiness
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(30) };

        // Act — JobsTestHost.StartAsync returns only once the scheduler reports ready
        await using var host = await JobsTestHost.StartAsync(WarmEveryFiveMinutesAndAtStart, spy, timeProvider);

        // Assert
        await spy.WaitForStartedAsync(1);
        spy.StartedCount.Should().Be(1);
        spy.Count.Should().Be(0, "the start-up run is still running");
    }

    private static void WarmEveryFiveMinutes(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));

    private static void WarmEveryFiveMinutesAndAtStart(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).RunAtStart().Run(() => new RecordedJob("warm"));
}
