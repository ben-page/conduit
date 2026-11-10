using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// <see cref="IJobs"/> without a persistence engine answers the durable API's calls the durable way where
/// it can, so code written against the durable runner behaves the same in a test host.
/// </summary>
[TestClass]
public class InMemoryJobsShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelByRunKey()
    {
        // Arrange — a manual run in progress; the clock is frozen, so its run key is known in advance
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var host = await JobsTestHost.StartAsync(RederiveOnDemand, spy, timeProvider);
        var jobs = host.GetRequiredService<IJobs>();
        var trigger = Task.Run(() => jobs.TriggerNowAsync("rederive", CancellationToken.None));
        await spy.WaitForStartedAsync(1);

        // Act — the key TriggerNowAsync returns, as code written against the durable runner passes it
        var outcome = await jobs.CancelAsync(ManualRunKey.Build("rederive", Start), CancellationToken.None);

        // Assert
        outcome.Should().Be(CancellationOutcome.Requested);
        await trigger.Awaiting(running => running).Should().ThrowAsync<OperationCanceledException>();
        spy.Count.Should().Be(0, "the handler was cancelled before it finished");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelByScheduleName()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var host = await JobsTestHost.StartAsync(RederiveOnDemand, spy, timeProvider);
        var jobs = host.GetRequiredService<IJobs>();
        var trigger = Task.Run(() => jobs.TriggerNowAsync("rederive", CancellationToken.None));
        await spy.WaitForStartedAsync(1);

        // Act — the schedule's name still reaches the run in progress on this host
        var outcome = await jobs.CancelAsync("rederive", CancellationToken.None);

        // Assert
        outcome.Should().Be(CancellationOutcome.Requested);
        await trigger.Awaiting(running => running).Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task CancelAScheduledRunByItsOccurrenceKey()
    {
        // Arrange — the run of the 02:00 occurrence, in progress
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy { JobDuration = TimeSpan.FromMinutes(10) };
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm")),
            spy,
            timeProvider);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        await spy.WaitForStartedAsync(1);

        // Act — the occurrence's key, shaped as the durable runner shapes it
        var outcome = await host.GetRequiredService<IJobs>().CancelAsync(
            DurableJobScheduler.BuildJobKey("warm", Start.AddMinutes(1)), CancellationToken.None);

        // Assert
        outcome.Should().Be(CancellationOutcome.Requested);
        await Task.Delay(100);
        spy.Count.Should().Be(0);
    }

    private static void RederiveOnDemand(JobsBuilder jobs)
        => jobs.Schedule("rederive").OnDemand().Run(() => new RecordedJob("rederive"));
}
