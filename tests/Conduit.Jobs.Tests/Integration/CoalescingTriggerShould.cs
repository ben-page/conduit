using System.Text.Json;
using AwesomeAssertions;
using Conduit.Jobs.Persistence;
using Conduit.Jobs.Tests.Support;
using Conduit.Runtime;
using Conduit.TestUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Conduit.Jobs.Tests.Integration;

[TestClass]
public class CoalescingTriggerShould
{
    private static readonly DateTimeOffset Start = new(2026, 8, 26, 1, 59, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task MergeABurstIntoOneScopeThatWidensTheWindow()
    {
        // Arrange — a delayed trigger, so nothing picks the scope up while the burst arrives
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, new FakeTimeProvider(Start), spy);
        var mediator = host.GetRequiredService<IMediator>();

        // Act — three punches at one workstation, arriving newest window first
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(10)));

        // Assert — one scope, and the merge kept the EARLIEST window: widen, never narrow
        var scope = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        scope.ScopeKey.Should().Be("rebuild@loc-1");
        scope.Version.Should().Be(2, "three events are one insert and two merges");
        scope.DueAt.Should().Be(Start.AddMinutes(30), "merging is leading-edge and never moves the due time");

        var merged = JsonSerializer.Deserialize<RecomputeWorkstation>(
            scope.Payload, ConduitRuntime.JsonOptions);
        merged!.From.Should().Be(Start.AddHours(8), "the merge keeps the earliest window: widen, never narrow");

        var runs = await JobsDatabaseProbe.CountRunsAsync(database);
        runs.Should().Be(0, "the scope is not due for another half hour");
    }

    [TestMethod]
    public async Task KeepScopesForDifferentKeysApart()
    {
        // Arrange
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartDelayedHostAsync(database, new FakeTimeProvider(Start), spy);
        var mediator = host.GetRequiredService<IMediator>();

        // Act
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await mediator.PublishAsync(new PunchChanged("loc-2", Start.AddHours(9)));

        // Assert — coalescing merges work that IS the same, and must not merge work that is not
        var store = JobsDatabaseProbe.StoreFor(database);
        var scopes = await store.GetScopesAsync(null, CancellationToken.None);
        scopes.Select(scope => scope.ScopeKey)
            .Should().BeEquivalentTo(["rebuild@loc-1", "rebuild@loc-2"]);
    }

    [TestMethod]
    public async Task SurviveTwoHostsMergingIntoOneScopeConcurrently()
    {
        // Arrange — two replicas of one application against one database. A local publish reaches only
        // the publishing host's listeners, so each host merges its own event into the shared scope.
        var timeProvider = new FakeTimeProvider(Start);
        await using var database = SharedSqliteDatabase.Create();
        await using var first = await StartDelayedHostAsync(database, timeProvider, new WorkstationSpy());
        await using var second = await StartDelayedHostAsync(database, timeProvider, new WorkstationSpy());

        // Act — both hosts race on one coalesce key
        await Task.WhenAll(
            first.GetRequiredService<IMediator>().PublishAsync(new PunchChanged("loc-1", Start.AddHours(9))),
            second.GetRequiredService<IMediator>().PublishAsync(new PunchChanged("loc-1", Start.AddHours(8))));

        // Assert — a lost update here would NARROW the window, which loses work rather than merely
        // duplicating it. The survivor must be the merge of both, never either input alone.
        var scope = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        scope.Version.Should().Be(1, "one host inserted and the other merged");

        JsonSerializer.Deserialize<RecomputeWorkstation>(scope.Payload, ConduitRuntime.JsonOptions)!
            .From.Should().Be(Start.AddHours(8), "whichever host won the insert, the earlier window survives");
    }

    [TestMethod]
    public async Task RunAnImmediateCoalescingTriggerOnTheReceivingHost()
    {
        // Arrange
        var spy = new WorkstationSpy();
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartImmediateHostAsync(database, new FakeTimeProvider(Start), spy);

        // Act
        await host.GetRequiredService<IMediator>()
            .PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));

        // Assert — latency matters: the host that received the event runs it, without waiting a sweep
        await spy.WaitForCountAsync(1);
        spy.Rebuilds.Should().ContainSingle().Which.LocationId.Should().Be("loc-1");
    }

    [TestMethod]
    public async Task ParkASecondScopeForEventsArrivingDuringARun()
    {
        // Arrange — the first rebuild blocks, so the burst behind it has somewhere to accumulate
        var spy = new WorkstationSpy { BlockUntilReleased = true };
        var timeProvider = new FakeTimeProvider(Start);
        await using var database = SharedSqliteDatabase.Create();
        await using var host = await StartImmediateHostAsync(database, timeProvider, spy);
        var mediator = host.GetRequiredService<IMediator>();

        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));
        await spy.WaitForCountAsync(1);

        // Act — three more punches while the first rebuild is in flight
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(8)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(7)));
        await mediator.PublishAsync(new PunchChanged("loc-1", Start.AddHours(11)));

        // Assert — a claimed run's scope is fixed, so the three form ONE new scope, not three runs
        var scope = await JobsDatabaseProbe.WaitForOnlyScopeAsync(database);
        scope.Version.Should().Be(2);
        JsonSerializer.Deserialize<RecomputeWorkstation>(scope.Payload, ConduitRuntime.JsonOptions)!
            .From.Should().Be(Start.AddHours(7));

        spy.Count.Should().Be(1, "the second run starts only once the first releases");
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(
            1, "the two-row promise is about the ledger, not the handler");

        // The waiting scope becomes the second run once the sweep drains scopes — see Task 6's
        // RunTheWaitingScopeOnceTheRunAheadOfItClears.
        spy.BlockUntilReleased = false;
        spy.ReleaseOne();
    }

    [TestMethod]
    public async Task CollapseASecondPickupOfOneScopeVersion()
    {
        // Arrange — a host dying between materializing a run and deleting its scope leaves the scope
        // alive, so the next sweep picks the same version up again. That retry must not double-run.
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        static JobRun Run() => new()
        {
            JobKey = "rebuild@loc-1",
            ScheduleName = "rebuild",
            MessageType = "Conduit.Jobs.Tests.Support.RecomputeWorkstation",
            Payload = """{"locationId":"loc-1"}""",
            RunAt = Start,
            Status = JobRunStatus.Pending,
        };

        // Act — the run key is the scope key alone, so both attempts compute the same key
        var first = await store.TryMaterializeAsync(Run(), CancellationToken.None);
        var second = await store.TryMaterializeAsync(Run(), CancellationToken.None);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        (await JobsDatabaseProbe.CountRunsAsync(database)).Should().Be(1);
    }

    [TestMethod]
    public async Task RunCoalescedWorkAfterATerminalRunHasHeldItsKey()
    {
        // Arrange — a run that exhausts its attempts (or is cancelled) goes terminal but stays in the
        // ledger until the retention purge, days later by default. The run key IS the coalesce key, so
        // that row must not be able to wedge every later unit of work behind it. Driven through the
        // store rather than a genuinely failing handler, so this stays fast and deterministic.
        await using var database = SharedSqliteDatabase.Create();
        var store = JobsDatabaseProbe.StoreFor(database);
        await store.EnsureTablesExistAsync(CancellationToken.None);

        await store.TryMaterializeAsync(
            new JobRun
            {
                JobKey = "rebuild@loc-1",
                ScheduleName = "rebuild",
                MessageType = "Conduit.Jobs.Tests.Support.RecomputeWorkstation",
                Payload = """{"locationId":"loc-1","from":"2026-08-25T10:59:00+00:00"}""",
                RunAt = Start,
                Status = JobRunStatus.Pending,
            },
            CancellationToken.None);
        var stuck = await store.GetRunAsync("rebuild@loc-1", CancellationToken.None);
        await store.MarkTerminalAsync(
            stuck!.Id, JobRunStatus.Failed, "boom", scheduleName: null, occurrence: null,
            CancellationToken.None);

        // Blocked, so the assertion below observes the new run in flight rather than racing its own
        // completion — CompleteAsync deletes the row on success, which would make "gone" ambiguous.
        var spy = new WorkstationSpy { BlockUntilReleased = true };
        await using var host = await StartImmediateHostAsync(database, new FakeTimeProvider(Start), spy);

        // Act — a different unit of work arrives for the same coalesce key
        await host.GetRequiredService<IMediator>()
            .PublishAsync(new PunchChanged("loc-1", Start.AddHours(9)));

        // Assert — the exhausted occurrence must not stall a scope that is different work
        await spy.WaitForCountAsync(1);
        spy.Rebuilds.Should().ContainSingle().Which.LocationId.Should().Be("loc-1");

        // JobKey is unique, so one row existing at Claiming means the Failed row is gone, superseded
        // by this new, already-running run rather than merely left in place beside it.
        var run = await store.GetRunAsync("rebuild@loc-1", CancellationToken.None);
        run.Should().NotBeNull("the terminal row was superseded by the new, already-running run");
        run!.Status.Should().Be(JobRunStatus.Claiming, "this is the new run, not the failed one left in place");

        spy.ReleaseOne();
    }

    private static Task<TestHost> StartDelayedHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, WorkstationSpy spy)
        => JobsTestHost.StartAsync(
            jobs => jobs.On<PunchChanged>("rebuild")
                .After(TimeSpan.FromMinutes(30))
                .CoalesceBy(punch => punch.LocationId)
                .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
                .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming),
            timeProvider: timeProvider,
            connectionString: database.ConnectionString,
            workstationSpy: spy);

    private static Task<TestHost> StartImmediateHostAsync(
        SharedSqliteDatabase database, FakeTimeProvider timeProvider, WorkstationSpy spy)
        => JobsTestHost.StartAsync(
            jobs => jobs.On<PunchChanged>("rebuild")
                .CoalesceBy(punch => punch.LocationId)
                .Run(punch => new RecomputeWorkstation(punch.LocationId, punch.From))
                .Merge((pending, incoming) => pending.From <= incoming.From ? pending : incoming),
            timeProvider: timeProvider,
            connectionString: database.ConnectionString,
            workstationSpy: spy);
}
