using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// A database error on one of a schedule's own store calls — a deadlock victim, a dropped connection —
/// is logged and the schedule's loop carries on. Escaping, it ended that loop for good: the scheduler
/// is one <c>BackgroundService</c> awaiting every loop together, so the host kept running with the
/// schedule silently stopped, and the failure surfaced only at shutdown.
/// </summary>
[TestClass]
public class SchedulerStoreErrorShould
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task KeepItsTimetable_AndLeaveTheRunToTheSweep_WhenAnOccurrencesClaimFails()
    {
        // Arrange — an hourly schedule whose sweep sleeps 90 minutes between passes, so the occurrence
        // at 02:59 is claimed by the schedule's own loop alone, and the sweep's next pass is at 03:29
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        var logs = new CapturingLoggerProvider();
        var fault = new JobStoreFault(nameof(IJobStore.ClaimDueAsync));
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.ClaimTimeout = TimeSpan.FromMinutes(90);
                jobs.Schedule("warm").Every(TimeSpan.FromHours(1)).Run(() => new RecordedJob("warm"));
            },
            spy,
            timeProvider,
            database.ConnectionString,
            logs,
            configureServices: services => FaultingJobStore.Install(services, fault));
        fault.Arm();

        // Act — the occurrence's claim throws
        timeProvider.Advance(TimeSpan.FromHours(1));

        // Assert — logged and left queued; the sweep's next pass runs it
        await logs.WaitForWarningAsync(message => message.Contains("could not claim its queued run", StringComparison.Ordinal));
        spy.Count.Should().Be(0, "nothing claimed the run yet");
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await spy.WaitForCountAsync(1);
        await JobsDatabaseProbe.WaitForDeletionAsync(database, DurableJobScheduler.BuildJobKey("warm", Start.AddHours(1)));

        // Assert — and the schedule's loop is still alive for the next occurrence, at 03:59
        timeProvider.Advance(TimeSpan.FromMinutes(30));
        await spy.WaitForCountAsync(2);
        host.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested
            .Should().BeFalse();
    }

    [TestMethod]
    public async Task LogNothing_WhenAnOccurrencesClaimFailsBecauseTheHostIsStopping()
    {
        // Arrange — the occurrence's claim is still running when the host stops, and fails the way
        // SqlClient reports the stop: as an error, not as cancellation
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var logs = new CapturingLoggerProvider();
        var fault = new JobStoreFault(nameof(IJobStore.ClaimDueAsync)) { ThrowsOnceCancelled = true };
        await using var host = await JobsTestHost.StartAsync(
            jobs =>
            {
                jobs.ClaimTimeout = TimeSpan.FromMinutes(90);
                jobs.Schedule("warm").Every(TimeSpan.FromHours(1)).Run(() => new RecordedJob("warm"));
            },
            new JobSpy(),
            timeProvider,
            database.ConnectionString,
            logs,
            configureServices: services => FaultingJobStore.Install(services, fault));
        fault.Arm();
        timeProvider.Advance(TimeSpan.FromHours(1));
        await fault.Taken.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await host.DisposeAsync();

        // Assert
        logs.Warnings.Should().BeEmpty("a store error while stopping is the stop's cancellation, not a failure");
    }

    [TestMethod]
    public async Task KeepItsTimetable_WhenItsCursorCannotBeRead()
    {
        // Arrange — the schedule's first store call, the cursor read its loop starts with, throws
        await using var database = SharedSqliteDatabase.Create();
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        var logs = new CapturingLoggerProvider();
        var fault = new JobStoreFault(nameof(IJobStore.GetScheduleAsync));

        // Armed before the start without an ordering assumption: only a schedule's loop calls
        // GetScheduleAsync (the sweep never does), and the cursor read is that loop's first call.
        fault.Arm();

        // Act
        await using var host = await JobsTestHost.StartAsync(
            jobs => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm")),
            spy,
            timeProvider,
            database.ConnectionString,
            logs,
            configureServices: services => FaultingJobStore.Install(services, fault));

        // Assert — logged, and the timetable runs from the host's start as for a schedule with no cursor
        await logs.WaitForWarningAsync(message => message.Contains("could not read its cursor", StringComparison.Ordinal));
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await spy.WaitForCountAsync(1);
        host.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested
            .Should().BeFalse();
    }
}
