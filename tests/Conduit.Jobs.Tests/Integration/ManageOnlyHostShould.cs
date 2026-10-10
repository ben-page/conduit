using System.Text.Json;
using AwesomeAssertions;
using Conduit.Jobs.Configuration;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Runtime;
using Conduit.Jobs.Tests.Support;
using Conduit.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

/// <summary>
/// A host with <see cref="JobsOptions.ManageOnly"/> set shares the ledger with a host that runs jobs,
/// and must operate it through <see cref="IJobs"/> without ever executing anything itself.
/// </summary>
[TestClass]
public class ManageOnlyHostShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 1, 59, 0, TimeSpan.Zero);
    private static readonly TimeSpan ClaimTimeout = new JobsOptions().ClaimTimeout;

    [TestMethod]
    public async Task NeverClaimADueRunAnotherHostMaterialized()
    {
        // Arrange — a due run in the shared ledger, as the runner host leaves it between
        // materializing an occurrence and claiming it
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);
        await store.TryMaterializeAsync(DueRun("warm@due"), CancellationToken.None);
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();

        await using (var manageOnly = await JobsTestHost.StartAsync(
            ManageOnlyWarm, spy, timeProvider, database.ConnectionString))
        {
            // Act — a runner's sweep claims at startup and wakes at least every ClaimTimeout, so this
            // outlasts any sweep there could be
            timeProvider.Advance(ClaimTimeout * 3);
            await Task.Delay(500);

            // Assert
            var run = await store.GetRunAsync("warm@due", CancellationToken.None);
            run!.Status.Should().Be(JobRunStatus.Pending);
            run.ClaimToken.Should().BeNull();
            spy.StartedCount.Should().Be(0);
            manageOnly.GetRequiredService<IEnumerable<IHostedService>>()
                .Select(service => service.GetType())
                .Should().NotContain([typeof(DurableJobScheduler), typeof(JobCleanup), typeof(InMemoryJobScheduler)]);
        }

        // Assert — the control: the same row is claimable, and a runner host takes it at once, so the
        // assertions above could have failed
        await using var runner = await JobsTestHost.StartAsync(
            RunnerWarm, spy, timeProvider, database.ConnectionString);
        await spy.WaitForCountAsync(1);
    }

    [TestMethod]
    public async Task TriggerAScheduleTheRunnerHostRuns()
    {
        // Arrange — the two hosts declare the same schedule, as the replicas of one application do
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var runner = await JobsTestHost.StartAsync(
            RunnerWarm, spy, timeProvider, database.ConnectionString);
        await using var manageOnly = await JobsTestHost.StartAsync(
            ManageOnlyWarm, spy, timeProvider, database.ConnectionString);
        var jobs = manageOnly.GetRequiredService<IJobs>();

        // Act
        await jobs.TriggerNowAsync("warm", CancellationToken.None);
        await FakeClock.AdvanceUntilAsync(
            timeProvider, TimeSpan.FromSeconds(30), () => Task.FromResult(spy.Count > 0));

        // Assert — the manage-only host wrote the run; only the runner could have executed it
        spy.Count.Should().BeGreaterThan(0);
        (await jobs.GetSchedulesAsync(CancellationToken.None)).Should()
            .ContainSingle(schedule => schedule.Name == "warm");
    }

    [TestMethod]
    public async Task PauseAScheduleTheRunnerHostRuns()
    {
        // Arrange
        var timeProvider = new FakeTimeProvider(Start);
        var spy = new JobSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var runner = await JobsTestHost.StartAsync(
            RunnerWarm, spy, timeProvider, database.ConnectionString);
        await using var manageOnly = await JobsTestHost.StartAsync(
            ManageOnlyWarm, spy, timeProvider, database.ConnectionString);
        var jobs = manageOnly.GetRequiredService<IJobs>();

        // Act
        await jobs.PauseAsync("warm", CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(300);

        // Assert — the pause lives in the shared table, so it reaches the host that runs the job
        spy.Count.Should().Be(0);
        (await jobs.GetSchedulesAsync(CancellationToken.None)).Should()
            .ContainSingle(schedule => schedule.Name == "warm" && schedule.IsPaused);
    }

    [TestMethod]
    public async Task ResolveAnIJobsThatListsNothingWithoutAnEngine()
    {
        // Arrange — a host that must resolve IJobs, has no engine, and declares nothing
        await using var host = await JobsTestHost.StartAsync(jobs => jobs.ManageOnly = true);

        // Act
        var jobs = host.GetRequiredService<IJobs>();
        var schedules = await jobs.GetSchedulesAsync(CancellationToken.None);

        // Assert
        schedules.Should().BeEmpty();
        (await jobs.GetPendingAsync(JobFilter.All, CancellationToken.None)).Should().BeEmpty();
        host.GetRequiredService<IEnumerable<IHostedService>>()
            .Select(service => service.GetType())
            .Should().NotContain(typeof(InMemoryJobScheduler));
    }

    private static void ManageOnlyWarm(JobsBuilder jobs)
    {
        jobs.ManageOnly = true;
        RunnerWarm(jobs);
    }

    private static void RunnerWarm(JobsBuilder jobs)
        => jobs.Schedule("warm").Every(TimeSpan.FromMinutes(5)).Run(() => new RecordedJob("warm"));

    private static JobRun DueRun(string jobKey) => new()
    {
        JobKey = jobKey,
        ScheduleName = "warm",
        Occurrence = null,
        MessageType = typeof(RecordedJob).FullName!,
        Payload = JsonSerializer.Serialize(new RecordedJob("warm"), ConduitRuntime.JsonOptions),
        RunAt = Start.AddMinutes(-1),
        Status = JobRunStatus.Pending,
    };
}
